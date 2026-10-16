using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApolloBreakdowns.Api;
using ApolloBreakdowns.Data;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Domain;

public sealed record SubmitBreakdown(Guid RequestId, int SiteId, int ShiftId, string Person,
    int MachineId, int PartId, int NodeId, int FaultId, string? Description, int RepairMethodId, bool? HasReserveApollo);
public sealed class RuleException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class BreakdownService(ApolloDbContext db, IConfiguration config)
{
    public async Task<Breakdown> SubmitAsync(SubmitBreakdown input, AppUser user, CancellationToken ct)
    {
        input = input with { Person = (input.Person ?? "").Trim(), Description = (input.Description ?? "").Trim() };
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
        var previous = await db.Breakdowns.SingleOrDefaultAsync(x => x.TelegramUserId == user.Id && x.RequestId == input.RequestId, ct);
        if (previous is not null) return SameRequest(previous, fingerprint);
        if (input.RequestId == Guid.Empty) throw new RuleException("Відсутній ідентифікатор заявки.");
        if (input.Person.Length is < 2 or > 300) throw new RuleException("Вкажіть ПІБ працівника (2–300 символів).");
        if (input.Description!.Length > 4000) throw new RuleException("Опис має містити до 4000 символів.");
        var ids = new[] { input.SiteId, input.ShiftId, input.MachineId, input.PartId, input.NodeId, input.FaultId, input.RepairMethodId };
        var options = await db.Options.Where(x => ids.Contains(x.Id) && x.Active).ToListAsync(ct);
        FieldOption Pick(int id, string kind) => options.SingleOrDefault(x => x.Id == id && x.Kind == kind)
            ?? throw new RuleException("Варіанти форми змінилися. Оновіть довідник і перевірте вибір.", 409);
        var site = Pick(input.SiteId, "site"); var shift = Pick(input.ShiftId, "shift");
        var machine = Pick(input.MachineId, "machine"); var part = Pick(input.PartId, "part");
        var node = Pick(input.NodeId, "node"); var fault = Pick(input.FaultId, "fault");
        var repair = Pick(input.RepairMethodId, "repairMethod");
        if (repair.RequiresReserve && input.HasReserveApollo is null) throw new RuleException("Вкажіть, чи є резервне APOLLO.");
        if (node.ParentId != part.Id || fault.ParentId != node.Id) throw new RuleException("Несправність не відповідає вибраному вузлу та частині.");
        if (new[] { part, node, fault }.Any(x => x.RequiresDescription) && input.Description.Length == 0)
            throw new RuleException("Для варіанта «Інше» додайте опис несправності.");
        var report = new Breakdown {
            RequestId = input.RequestId, Fingerprint = fingerprint, TelegramUserId = user.Id, TelegramName = user.Name,
            Site = site.Label, Shift = shift.Label, Person = input.Person, Machine = machine.Label,
            Part = part.Label, Node = node.Label, Fault = fault.Label, Description = input.Description,
            RepairMethod = repair.Label, HasReserveApollo = repair.RequiresReserve ? input.HasReserveApollo : null
        };
        db.Breakdowns.Add(report);
        AddNotifications(report, "new", $"🟠 Нова несправність APOLLO\n{Summary(report)}\n\nВідлік простою розпочато. Після відновлення роботи натисніть кнопку нижче.");
        // One SaveChanges atomically inserts the report and the author/group outbox messages.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            previous = await db.Breakdowns.SingleOrDefaultAsync(x => x.TelegramUserId == user.Id && x.RequestId == input.RequestId, ct);
            if (previous is null) throw;
            return SameRequest(previous, fingerprint);
        }
        return report;
    }

    private static Breakdown SameRequest(Breakdown report, string fingerprint) => report.Fingerprint == fingerprint
        ? report : throw new RuleException("Ідентифікатор вже використано для іншої заявки. Створіть нову заявку.", 409);

    public async Task<Breakdown> RestoreAsync(int id, AppUser user, CancellationToken ct, bool fromConfiguredGroup = false)
    {
        var report = await db.Breakdowns.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new RuleException("Заявку не знайдено.", 404);
        if (report.TelegramUserId != user.Id && !user.IsAdmin && !fromConfiguredGroup)
            throw new RuleException("Завершити простій може автор, адміністратор або учасник групи APOLLO.", 403);
        if (report.RestoredAt is not null) return report;
        report.RestoredAt = DateTimeOffset.UtcNow;
        report.RestoredByUserId = user.Id;
        report.Version = Guid.NewGuid();
        var duration = FormatDuration(DowntimeSeconds(report, DateTimeOffset.UtcNow));
        var notifications = AddNotifications(report, "restored", $"✅ Роботу відновлено\nЗаявка №{report.Id}\n{report.Machine} · {report.Site}\nНесправність: {report.Fault}\nЧас простою: {duration}\nВідновлення зафіксував: {user.Name} (ID {user.Id})");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) {
            foreach (var notification in notifications) db.Entry(notification).State = EntityState.Detached;
            await db.Entry(report).ReloadAsync(ct);
            if (report.RestoredAt is null) throw;
        }
        return report;
    }

    private List<Notification> AddNotifications(Breakdown report, string kind, string text)
    {
        var targets = new List<long> { report.TelegramUserId };
        var group = config.GetValue<long>("Telegram:GroupChatId");
        if (group < 0) targets.Add(group);
        var items = targets.Distinct().Select(chat => new Notification {
            Breakdown = report, ChatId = chat, Kind = kind, Text = text,
            SentAt = config["Telegram:Mode"] == "Demo" ? DateTimeOffset.UtcNow : null
        }).ToList();
        db.Notifications.AddRange(items);
        return items;
    }

    private static string Summary(Breakdown report) => $"{report.Site} · зміна {report.Shift}\n{report.Machine}\n{report.Part} / {report.Node}\nНесправність: {report.Fault}\nПрацівник: {report.Person}\nРемонт: {report.RepairMethod}" +
        (report.HasReserveApollo is bool reserve ? $"\nРезервне APOLLO: {(reserve ? "Так" : "Ні")}" : "") +
        (report.Description.Length > 0 ? $"\nОпис: {report.Description[..Math.Min(report.Description.Length, 700)]}" : "");

    public static long DowntimeSeconds(Breakdown report, DateTimeOffset now) => Math.Max(0, (long)((report.RestoredAt ?? now) - report.CreatedAt).TotalSeconds);
    public static string FormatDuration(long seconds) => seconds < 60 ? "менше 1 хв" : $"{seconds / 60} хв";
}
