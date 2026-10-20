using ApolloBreakdowns.Data;
using ApolloBreakdowns.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Telegram;

public sealed class ReminderWorker(IServiceScopeFactory scopes, StorageRuntime storage, IConfiguration config,
    ILogger<ReminderWorker> logger) : ResilientBackgroundService(logger)
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        await storage.WaitAsync(ct);
        if (config["Telegram:Mode"] == "Demo") return;
        while (!ct.IsCancellationRequested)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApolloDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<ReminderService>();
            var telegram = scope.ServiceProvider.GetRequiredService<TelegramClient>();
            await service.QueueDueAsync(DateTimeOffset.UtcNow, ct);
            var now = DateTimeOffset.UtcNow;
            var pending = await db.ReminderDeliveries.Where(x => x.SentAt == null && x.NextAttemptAt <= now).Take(10).ToListAsync(ct);
            foreach (var item in pending)
            {
                item.Version = Guid.NewGuid(); item.Attempts++; item.NextAttemptAt = now.AddMinutes(2);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateConcurrencyException) { db.Entry(item).State = EntityState.Detached; continue; }
                try
                {
                    var enabled = await db.Reminders.AnyAsync(x => x.Id == item.ScheduleId && x.Active && !x.Deleted, ct);
                    var text = enabled ? await service.BuildTextAsync(now, ct) : null;
                    // Recheck open reports before retries: never send an empty or outdated reminder.
                    if (text is not null) await telegram.SendAsync(item.ChatId, text, ct);
                    item.SentAt = DateTimeOffset.UtcNow;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    logger.LogWarning("Reminder delivery failed ({ErrorType}); retrying later.", e.GetType().Name);
                    item.NextAttemptAt = now.AddMinutes(Math.Min(60, item.Attempts * 2));
                }
                item.Version = Guid.NewGuid();
                await db.SaveChangesAsync(ct);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
