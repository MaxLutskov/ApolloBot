using System.Text.Json;
using ApolloBreakdowns.Api;
using ApolloBreakdowns.Domain;

namespace ApolloBreakdowns.Telegram;

// Based on VPF_Algorithm_Bot/Telegram/TelegramClient.cs. No bot token is logged.
public sealed class TelegramClient(HttpClient http, IConfiguration config, BreakdownService breakdowns, UserDirectory users)
{
    public async Task<JsonElement> CallAsync(string method, object payload, CancellationToken ct)
    {
        var token = config["Telegram:BotToken"] ?? throw new InvalidOperationException("Bot token missing");
        using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method}", payload, ct);
        if (!response.IsSuccessStatusCode) throw new TelegramException((int)response.StatusCode);
        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct) ?? throw new TelegramException(502);
        if (!json.RootElement.GetProperty("ok").GetBoolean()) throw new TelegramException(502);
        return json.RootElement.GetProperty("result").Clone();
    }

    public Task<JsonElement> SendAsync(long chatId, string text, CancellationToken ct) => CallAsync("sendMessage", new { chat_id = chatId, text }, ct);

    public Task<JsonElement> SendNotificationAsync(long chatId, string text, int reportId, bool showRestore, CancellationToken ct, bool startRepair = false) =>
        showRestore && chatId < 0 ? CallAsync("sendMessage", new { chat_id = chatId, text = $"Заявка №{reportId}\n{text}",
            reply_markup = Keyboard(reportId, startRepair) }, ct)
        : SendAsync(chatId, text, ct);

    public static object Keyboard(int reportId, bool startRepair) => new { inline_keyboard = new[] { new[] { new {
        text = startRepair ? "🔧 Почато ремонт" : "✅ Проблему вирішено", callback_data = $"{(startRepair ? "start" : "restore")}:{reportId}"
    } } } };

    public async Task SendDocumentAsync(long chatId, byte[] bytes, CancellationToken ct, bool testMode = false)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
        var document = new ByteArrayContent(bytes);
        document.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(document, "document", testMode ? "Apollo-test-history.csv" : "Apollo-history.csv");
        using var response = await http.PostAsync($"https://api.telegram.org/bot{config["Telegram:BotToken"]}/sendDocument", content, ct);
        if (!response.IsSuccessStatusCode) throw new TelegramException((int)response.StatusCode);
        using var result = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
        if (result?.RootElement.GetProperty("ok").GetBoolean() != true) throw new TelegramException(502);
    }

    public async Task HandleUpdateAsync(JsonElement update, CancellationToken ct)
    {
        if (update.TryGetProperty("callback_query", out var callback)) {
            await HandleCallbackAsync(callback, ct); return;
        }
        if (update.TryGetProperty("message", out var incoming) && incoming.TryGetProperty("from", out var visitor) &&
            !(visitor.TryGetProperty("is_bot", out var bot) && bot.GetBoolean()))
            await users.VisitTelegramAsync(visitor.GetProperty("id").GetInt64(), TelegramName(visitor), ct);
        if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("text", out var text) ||
            !message.TryGetProperty("chat", out var chat)) return;
        var command = text.GetString()?.Split(' ', '@')[0];
        if (command == "/chatid" && message.TryGetProperty("from", out var sender) &&
            (await users.VisitTelegramAsync(sender.GetProperty("id").GetInt64(), TelegramName(sender), ct)).IsAdmin) {
            await SendAsync(chat.GetProperty("id").GetInt64(), $"Chat ID: {chat.GetProperty("id").GetInt64()}", ct); return;
        }
        if (chat.GetProperty("type").GetString() != "private") return;
        if (command is not ("/start" or "/help")) return;
        var publicUrl = config["Telegram:PublicUrl"];
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri) || publicUri.Scheme != "https")
        {
            await SendAsync(chat.GetProperty("id").GetInt64(), "Бот працює. Адресу форми ще не налаштовано.", ct);
            return;
        }
        var url = publicUrl!.TrimEnd('/') + "/miniapp/";
        await CallAsync("sendMessage", new {
            chat_id = chat.GetProperty("id").GetInt64(),
            text = "Облік поломок APOLLO\nВідкрийте форму, заповніть дані та надішліть заявку. Після збереження ви отримаєте підтвердження. Історія доступна у формі.",
            reply_markup = new { inline_keyboard = new[] { new[] { new { text = "Відкрити форму", web_app = new { url } } } } }
        }, ct);
    }

    private static string TelegramName(JsonElement user) => string.Join(' ', new[] { "first_name", "last_name" }
        .Select(key => user.TryGetProperty(key, out var name) ? name.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)));

    private async Task DismissConfirmationAsync(long chatId, int messageId, CancellationToken ct)
    {
        try { await CallAsync("deleteMessage", new { chat_id = chatId, message_id = messageId }, ct); }
        catch (TelegramException) {
            // If Telegram disallows deletion, at least remove the obsolete buttons.
            try { await CallAsync("editMessageReplyMarkup", new { chat_id = chatId, message_id = messageId, reply_markup = new { inline_keyboard = Array.Empty<object>() } }, ct); }
            catch (TelegramException) { }
        }
    }

    private async Task HandleCallbackAsync(JsonElement callback, CancellationToken ct)
    {
        var callbackId = callback.GetProperty("id").GetString()!;
        var text = "Невідома дія.";
        try {
            var data = callback.TryGetProperty("data", out var d) ? d.GetString() : null;
            if (data is null || !callback.TryGetProperty("message", out var message)) return;
            var from = callback.GetProperty("from");
            var userId = from.GetProperty("id").GetInt64();
            if (userId <= 0 || from.TryGetProperty("is_bot", out var isBot) && isBot.GetBoolean()) return;
            var actor = await users.VisitTelegramAsync(userId, TelegramName(from), ct);
            var allowed = config.GetSection("Telegram:AllowedUserIds").Get<long[]>() ?? [];
            if (allowed.Length > 0 && !allowed.Contains(userId) && !actor.IsAdmin) throw new RuleException("Немає доступу.", 403);
            var chatId = message.GetProperty("chat").GetProperty("id").GetInt64();
            var messageId = message.GetProperty("message_id").GetInt32();
            if (data.StartsWith("ok:") || data.StartsWith("no:")) {
                var confirmation = ActionConfirmation.Read(data[3..], config["Telegram:BotToken"] ?? "", userId, chatId, DateTimeOffset.UtcNow)
                    ?? throw new RuleException("Підтвердження недійсне, належить іншому користувачу або минуло 5 хвилин. Натисніть кнопку заявки ще раз.");
                if (data.StartsWith("no:")) {
                    text = "Дію скасовано.";
                    await DismissConfirmationAsync(chatId, messageId, ct);
                    return;
                }
                await breakdowns.SelectTelegramModeAsync(confirmation.ReportId, actor, chatId, ct);
                var report = confirmation.Start ? await breakdowns.StartRepairAsync(confirmation.ReportId, actor, ct) : await breakdowns.RestoreAsync(confirmation.ReportId, actor, ct);
                text = confirmation.Start ? "Початок ремонту зафіксовано." : $"Роботу відновлено. Простій машини: {BreakdownService.MachineDowntimeMinutes(report, DateTimeOffset.UtcNow)} хв; процесу: {BreakdownService.ProcessDowntimeMinutes(report, DateTimeOffset.UtcNow)} хв.";
                await DismissConfirmationAsync(chatId, messageId, ct);
                try { await CallAsync("editMessageReplyMarkup", new { chat_id = chatId, message_id = confirmation.MessageId, reply_markup = report.RestoredAt is null ? Keyboard(report.Id, report.RequiresRepairStart && report.RepairStartedAt is null) : (object)new { inline_keyboard = Array.Empty<object>() } }, ct); }
                catch (TelegramException) { }
            }
            else {
                var parts = data.Split(':');
                if (parts is not { Length: 2 } || parts[0] is not ("restore" or "start") || !int.TryParse(parts[1], out var reportId)) return;
                var start = parts[0] == "start";
                var report = await breakdowns.SelectTelegramModeAsync(reportId, actor, chatId, ct);
                if (report.RestoredAt is not null) throw new RuleException("Заявку вже завершено.");
                if (start && (!report.RequiresRepairStart || report.RepairStartedAt is not null)) throw new RuleException("Починати ремонт повторно не потрібно.");
                if (!start && report.RequiresRepairStart && report.RepairStartedAt is null) throw new RuleException("Спочатку зафіксуйте початок ремонту.");
                var token = new ActionConfirmation(reportId, userId, chatId, messageId, start).Sign(config["Telegram:BotToken"] ?? "", DateTimeOffset.UtcNow);
                await CallAsync("sendMessage", new {
                    chat_id = chatId,
                    text = (report.IsTest ? "🧪 ТЕСТ\n" : "") + $"{actor.Name}, підтвердьте {(start ? "початок ремонту" : "завершення робіт")} за заявкою №{reportId}\n{report.Machine} · {report.Site}" +
                        (start ? "" : "\nБуде завершено поточний простій процесу для всіх APOLLO без резерву."),
                    reply_markup = new { inline_keyboard = new[] { new[] {
                        new { text = "✅ Підтвердити", callback_data = "ok:" + token },
                        new { text = "Скасувати", callback_data = "no:" + token }
                    } } }
                }, ct);
                text = "Підтвердьте дію в повідомленні нижче.";
            }
        }
        catch (RuleException e) { text = e.Message; }
        finally { await CallAsync("answerCallbackQuery", new { callback_query_id = callbackId, text }, ct); }
    }
}

public sealed class TelegramException(int statusCode) : Exception($"Telegram API status {statusCode}");
