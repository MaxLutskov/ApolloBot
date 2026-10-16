using System.Text.Json;
using ApolloBreakdowns.Api;
using ApolloBreakdowns.Domain;

namespace ApolloBreakdowns.Telegram;

// Based on VPF_Algorithm_Bot/Telegram/TelegramClient.cs. No bot token is logged.
public sealed class TelegramClient(HttpClient http, IConfiguration config, BreakdownService breakdowns)
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

    public Task<JsonElement> SendNotificationAsync(long chatId, string text, int reportId, bool showRestore, CancellationToken ct) =>
        showRestore ? CallAsync("sendMessage", new { chat_id = chatId, text = $"Заявка №{reportId}\n{text}",
            reply_markup = new { inline_keyboard = new[] { new[] { new { text = "Роботу відновлено", callback_data = $"restore:{reportId}" } } } } }, ct)
        : SendAsync(chatId, text, ct);

    public async Task HandleUpdateAsync(JsonElement update, CancellationToken ct)
    {
        if (update.TryGetProperty("callback_query", out var callback)) {
            await HandleCallbackAsync(callback, ct); return;
        }
        if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("text", out var text) ||
            !message.TryGetProperty("chat", out var chat)) return;
        var command = text.GetString()?.Split(' ', '@')[0];
        if (command == "/chatid" && message.TryGetProperty("from", out var sender) &&
            (config.GetSection("Admin:TelegramIds").Get<long[]>() ?? []).Contains(sender.GetProperty("id").GetInt64())) {
            await SendAsync(chat.GetProperty("id").GetInt64(), $"Chat ID: {chat.GetProperty("id").GetInt64()}", ct); return;
        }
        if (chat.GetProperty("type").GetString() != "private") return;
        if (command is not ("/start" or "/help")) return;
        var url = config["Telegram:PublicUrl"]!.TrimEnd('/') + "/miniapp/";
        await CallAsync("sendMessage", new {
            chat_id = chat.GetProperty("id").GetInt64(),
            text = "Облік поломок APOLLO\nВідкрийте форму, заповніть дані та надішліть заявку. Після збереження ви отримаєте підтвердження. Історія доступна у формі.",
            reply_markup = new { inline_keyboard = new[] { new[] { new { text = "Відкрити форму", web_app = new { url } } } } }
        }, ct);
    }

    private async Task HandleCallbackAsync(JsonElement callback, CancellationToken ct)
    {
        var callbackId = callback.GetProperty("id").GetString()!;
        var text = "Невідома дія.";
        try {
            var data = callback.TryGetProperty("data", out var d) ? d.GetString() : null;
            if (data?.StartsWith("restore:", StringComparison.Ordinal) != true || !int.TryParse(data[8..], out var reportId)) return;
            var from = callback.GetProperty("from");
            var userId = from.GetProperty("id").GetInt64();
            if (userId <= 0 || from.TryGetProperty("is_bot", out var isBot) && isBot.GetBoolean()) return;
            var admins = config.GetSection("Admin:TelegramIds").Get<long[]>() ?? [];
            var name = from.TryGetProperty("first_name", out var n) ? n.GetString() ?? userId.ToString() : userId.ToString();
            var group = config.GetValue<long>("Telegram:GroupChatId");
            var hasMessage = callback.TryGetProperty("message", out var message);
            var chatId = hasMessage ? message.GetProperty("chat").GetProperty("id").GetInt64() : 0;
            var report = await breakdowns.RestoreAsync(reportId, new AppUser(userId, name, admins.Contains(userId)), ct, group < 0 && chatId == group);
            text = $"Роботу відновлено. Простій: {BreakdownService.FormatDuration(BreakdownService.DowntimeSeconds(report, DateTimeOffset.UtcNow))}.";
            if (hasMessage) {
                // Removing a button is best effort; database idempotency remains authoritative.
                try { await CallAsync("editMessageReplyMarkup", new { chat_id = chatId, message_id = message.GetProperty("message_id").GetInt32(), reply_markup = new { inline_keyboard = Array.Empty<object>() } }, ct); }
                catch (TelegramException) { }
            }
        }
        catch (RuleException e) { text = e.Message; }
        finally { await CallAsync("answerCallbackQuery", new { callback_query_id = callbackId, text }, ct); }
    }
}

public sealed class TelegramException(int statusCode) : Exception($"Telegram API status {statusCode}");
