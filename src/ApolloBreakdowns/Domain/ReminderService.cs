using System.Globalization;
using System.Text;
using ApolloBreakdowns.Data;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Domain;

public sealed class ReminderService(ApolloDbContext db, IConfiguration config)
{
    public static readonly TimeZoneInfo Kyiv = ApplicationTime.Kyiv;
    public static bool ValidTime(string time) => TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public async Task QueueDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var chat = config.GetValue<long>("Telegram:GroupChatId");
        if (chat >= 0) return;
        var local = TimeZoneInfo.ConvertTime(now, Kyiv);
        var day = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var schedules = await db.Reminders.Where(x => x.Active && !x.Deleted).ToListAsync(ct);
        foreach (var schedule in schedules)
        {
            if (!ValidTime(schedule.Time)) continue;
            var scheduled = TimeOnly.ParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture);
            var elapsed = local.TimeOfDay - scheduled.ToTimeSpan();
            // Ten-minute catch-up window; persisted delivery keys prevent duplicate runs after restarts/DST.
            if (elapsed < TimeSpan.Zero || elapsed >= TimeSpan.FromMinutes(10) ||
                await db.ReminderDeliveries.AnyAsync(x => x.ScheduleId == schedule.Id && x.LocalDate == day, ct)) continue;
            var text = await BuildTextAsync(now, ct);
            if (text is null) continue;
            db.ReminderDeliveries.Add(new ReminderDelivery { ScheduleId = schedule.Id, LocalDate = day, ChatId = chat, Text = text });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
    }

    public async Task<string?> BuildTextAsync(DateTimeOffset now, CancellationToken ct)
    {
        var total = await db.Breakdowns.CountAsync(x => x.RestoredAt == null, ct);
        if (total == 0) return null;
        var reports = await db.Breakdowns.AsNoTracking().Where(x => x.RestoredAt == null).OrderBy(x => x.Id).Take(15).ToListAsync(ct);
        var text = new StringBuilder($"⏰ Невиконані заявки APOLLO: {total}\n");
        foreach (var r in reports)
            text.Append($"\n#{r.Id} · {Short(r.Site, 40)} · {Short(r.Machine, 40)}\n{Short(r.Fault, 80)} · простій {BreakdownService.FormatDowntime(BreakdownService.DowntimeSeconds(r, now))}\n{(r.RepairStartedAt is null ? "Очікує вирішення" : "Ремонт триває")}\n");
        if (total > reports.Count) text.Append($"\nЩе {total - reports.Count} заявок — у спільному журналі.");
        return text.ToString();
    }

    private static string Short(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
