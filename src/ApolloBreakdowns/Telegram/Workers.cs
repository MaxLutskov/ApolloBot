using System.Text.Json;
using ApolloBreakdowns.Data;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Telegram;

public sealed class TelegramWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<TelegramWorker> logger, StorageRuntime storage) : ResilientBackgroundService(logger)
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        await storage.WaitAsync(ct);
        var mode = config["Telegram:Mode"];
        if (mode == "Demo") return;
        var initialized = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var telegram = scope.ServiceProvider.GetRequiredService<TelegramClient>();
                var db = scope.ServiceProvider.GetRequiredService<ApolloDbContext>();
                if (!initialized)
                {
                    var url = config["Telegram:PublicUrl"]?.TrimEnd('/');
                    if (Uri.TryCreate(url, UriKind.Absolute, out var publicUri) && publicUri.Scheme == "https")
                        await telegram.CallAsync("setChatMenuButton", new { menu_button = new { type = "web_app", text = "Форма APOLLO", web_app = new { url = url + "/miniapp/" } } }, ct);
                    if (mode == "Webhook")
                        await telegram.CallAsync("setWebhook", new { url = url + "/api/telegram/webhook", secret_token = config["Telegram:WebhookSecret"], allowed_updates = new[] { "message", "callback_query" }, drop_pending_updates = false }, ct);
                    else await telegram.CallAsync("deleteWebhook", new { drop_pending_updates = false }, ct);
                    initialized = true;
                }
                if (mode == "Webhook") return;
                var state = await db.BotStates.SingleAsync(x => x.Id == 1, ct);
                var updates = await telegram.CallAsync("getUpdates", new { offset = state.Offset, timeout = 25, allowed_updates = new[] { "message", "callback_query" } }, ct);
                foreach (var update in updates.EnumerateArray())
                {
                    await telegram.HandleUpdateAsync(update, ct);
                    state.Offset = update.GetProperty("update_id").GetInt64() + 1;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Telegram initialization/polling failed; retry in 15 seconds."); await Task.Delay(TimeSpan.FromSeconds(15), ct); }
        }
    }
}

public sealed class NotificationWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<NotificationWorker> logger, StorageRuntime storage) : ResilientBackgroundService(logger)
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        await storage.WaitAsync(ct);
        if (config["Telegram:Mode"] == "Demo") return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApolloDbContext>();
                var telegram = scope.ServiceProvider.GetRequiredService<TelegramClient>();
                var now = DateTimeOffset.UtcNow;
                var items = await db.Notifications.Where(x => x.SentAt == null && x.NextAttemptAt <= now).OrderBy(x => x.Id).Take(10).ToListAsync(ct);
                foreach (var item in items)
                {
                    // Optimistic lease prevents two Azure instances from sending the same job concurrently.
                    item.NextAttemptAt = now.AddMinutes(2); item.Version = Guid.NewGuid(); item.Attempts++;
                    try { await db.SaveChangesAsync(ct); }
                    catch (DbUpdateConcurrencyException) { db.Entry(item).State = EntityState.Detached; continue; }
                    try {
                        var report = await db.Breakdowns.AsNoTracking().SingleAsync(x => x.Id == item.BreakdownId, ct);
                        await telegram.SendNotificationAsync(item.ChatId, item.Text, item.BreakdownId, item.Kind == "new" && report.RestoredAt is null, ct);
                        item.SentAt = DateTimeOffset.UtcNow;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch {
                        item.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(3600, Math.Pow(2, Math.Min(item.Attempts, 10)) * 15));
                        logger.LogWarning("Confirmation for report {ReportId} delayed; attempt {Attempt}.", item.BreakdownId, item.Attempts);
                    }
                    item.Version = Guid.NewGuid();
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Notification queue unavailable; retrying."); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
