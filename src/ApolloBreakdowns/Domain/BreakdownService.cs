using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApolloBreakdowns.Api;
using ApolloBreakdowns.Data;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Domain;

public sealed record SubmitBreakdown(Guid RequestId, int SiteId, int ShiftId, string Person,
    int MachineId, int PartId, int NodeId, int FaultId, string? Description, int RepairMethodId, bool? HasReserveApollo, string RequestType = "emergency");
public sealed class RuleException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class BreakdownService(ApolloDbContext db, IConfiguration config)
{
    public const string Emergency = "emergency";
    public const string Productivity = "productivity";
    private static readonly SemaphoreSlim ProcessChanges = new(1, 1);

    // Submission and restoration share a database lock across Azure instances, so the
    // first machine restored ends every process outage that existed at that instant.
    private async Task<Breakdown> ChangeProcessAsync(Func<Task<Breakdown>> change, CancellationToken ct)
    {
        await ProcessChanges.WaitAsync(ct);
        try
        {
            if (!db.Database.IsSqlServer()) return await change();
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = N'apollo-process-downtime', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; IF @result < 0 THROW 50001, 'Process downtime lock unavailable', 1;", ct);
                try {
                    var result = await change();
                    await transaction.CommitAsync(ct);
                    return result;
                }
                catch { db.ChangeTracker.Clear(); throw; }
            });
        }
        finally { ProcessChanges.Release(); }
    }

    public Task<Breakdown> FindAsync(int id, CancellationToken ct) => FindReportAsync(id, ct);
    private async Task<Breakdown> FindReportAsync(int id, CancellationToken ct) =>
        await db.Breakdowns.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new RuleException("Заявку не знайдено.", 404);

    public Task<Breakdown> SubmitAsync(SubmitBreakdown input, AppUser user, CancellationToken ct) =>
        ChangeProcessAsync(() => SubmitCoreAsync(input, user, ct), ct);

    private async Task<Breakdown> SubmitCoreAsync(SubmitBreakdown input, AppUser user, CancellationToken ct)
    {
        input = input with { Person = (input.Person ?? "").Trim(), Description = (input.Description ?? "").Trim() };
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
        var previous = await db.Breakdowns.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TelegramUserId == user.Id && x.RequestId == input.RequestId, ct);
        if (previous is not null) return SameRequest(previous, fingerprint);
        if (input.RequestType is not (Emergency or Productivity)) throw new RuleException("Оберіть тип заявки: аварійна або зниження продуктивності.");
        if (input.RequestId == Guid.Empty) throw new RuleException("Відсутній ідентифікатор заявки.");
        if (input.Person.Length is < 2 or > 300) throw new RuleException("Вкажіть ПІБ працівника (2–300 символів).");
        if (input.Description!.Length > 4000) throw new RuleException("Опис має містити до 4000 символів.");
        var ids = new[] { input.SiteId, input.ShiftId, input.MachineId, input.PartId, input.NodeId, input.FaultId, input.RepairMethodId };
        var options = await db.Options.Where(x => ids.Contains(x.Id) && x.Active && !x.Deleted).ToListAsync(ct);
        FieldOption Pick(int id, string kind) => options.SingleOrDefault(x => x.Id == id && x.Kind == kind)
            ?? throw new RuleException("Варіанти форми змінилися. Оновіть довідник і перевірте вибір.", 409);
        var site = Pick(input.SiteId, "site"); var shift = Pick(input.ShiftId, "shift");
        var machine = Pick(input.MachineId, "machine"); var part = Pick(input.PartId, "part");
        var node = Pick(input.NodeId, "node"); var fault = Pick(input.FaultId, "fault");
        var repair = Pick(input.RepairMethodId, "repairMethod");
        if (input.RequestType == Emergency && input.HasReserveApollo is null) throw new RuleException("Вкажіть, чи є резервне APOLLO.");
        if ((part.IsOther && !node.IsOther) || (node.IsOther && !fault.IsOther)) throw new RuleException("Для пункту «Інше» вкладені поля також мають бути «Інше».");
        if (node.ParentId != part.Id || fault.ParentId != node.Id) throw new RuleException("Несправність не відповідає вибраному вузлу та частині.");
        if (new[] { part, node, fault }.Any(x => x.RequiresDescription) && input.Description.Length == 0)
            throw new RuleException("Для варіанта «Інше» додайте опис несправності.");
        var report = new Breakdown {
            RequestId = input.RequestId, Fingerprint = fingerprint, TelegramUserId = user.Id, TelegramName = user.Name,
            Site = site.Label, Shift = shift.Label, Person = input.Person, Machine = machine.Label,
            Part = part.Label, Node = node.Label, Fault = fault.Label, Description = input.Description,
            RepairMethod = repair.Label, RequiresRepairStart = repair.RequiresRepairStart, RequestType = input.RequestType, HasReserveApollo = input.RequestType == Emergency ? input.HasReserveApollo : null
        };
        db.Breakdowns.Add(report);
        AddNotifications(report, "new", $"🟠 Нова заявка APOLLO\n{Summary(report)}\n\n{(report.RequestType == Productivity ? "Простій не нараховується." : "Відлік простою машини розпочато. " + (report.HasReserveApollo == true ? "Простій процесу: 20 хв." : "Простій процесу триває до відновлення будь-якого APOLLO."))}");
        // One SaveChanges atomically inserts the report and the author/group outbox messages.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            previous = await db.Breakdowns.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TelegramUserId == user.Id && x.RequestId == input.RequestId, ct);
            if (previous is null) throw;
            return SameRequest(previous, fingerprint);
        }
        return report;
    }

    private static Breakdown SameRequest(Breakdown report, string fingerprint)
    {
        if (report.DeletedAt is not null) throw new RuleException("Заявку видалено адміністратором. Для нової заявки оновіть форму.", 409);
        return report.Fingerprint == fingerprint ? report : throw new RuleException("Ідентифікатор вже використано для іншої заявки. Створіть нову заявку.", 409);
    }

    public async Task DeleteAsync(int id, AppUser actor, CancellationToken ct)
    {
        if (!actor.IsAdmin) throw new RuleException("Видаляти заявки може лише адміністратор.", 403);
        var report = await db.Breakdowns.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new RuleException("Заявку не знайдено.", 404);
        if (report.DeletedAt is not null) return;
        var now = DateTimeOffset.UtcNow;
        report.DeletedAt = now;
        report.DeletedByUserId = actor.Id;
        report.Version = Guid.NewGuid();
        var pending = await db.Notifications.Where(x => x.BreakdownId == id && x.SentAt == null && x.CancelledAt == null).ToListAsync(ct);
        foreach (var notification in pending) { notification.CancelledAt = now; notification.Version = Guid.NewGuid(); }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new RuleException("Заявка змінилася. Оновіть журнал і повторіть видалення.", 409); }
    }

    public Task<Breakdown> RestoreAsync(int id, AppUser user, CancellationToken ct, bool fromConfiguredGroup = false) =>
        ChangeProcessAsync(() => RestoreCoreAsync(id, user, ct), ct);

    private async Task<Breakdown> RestoreCoreAsync(int id, AppUser user, CancellationToken ct)
    {
        var report = await db.Breakdowns.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new RuleException("Заявку не знайдено.", 404);
        await db.Entry(report).ReloadAsync(ct);
        if (report.DeletedAt is not null) throw new RuleException("Заявку видалено адміністратором.", 404);
        if (report.RestoredAt is not null) return report;
        if (report.RequiresRepairStart && report.RepairStartedAt is null) throw new RuleException("Спочатку натисніть «Почато ремонт» після прибуття працівника.", 409);
        report.RestoredAt = DateTimeOffset.UtcNow;
        report.RestoredByUserId = user.Id;
        report.Version = Guid.NewGuid();
        var processOutages = await db.Breakdowns.Where(x => x.RequestType == Emergency && x.HasReserveApollo != true && x.ProcessRestoredAt == null).ToListAsync(ct);
        foreach (var outage in processOutages.Where(x => x.CreatedAt <= report.RestoredAt.Value)) {
            outage.ProcessRestoredAt = report.RestoredAt;
            outage.ProcessRestoredByBreakdownId = report.Id;
            outage.Version = Guid.NewGuid();
        }
        var duration = MachineDowntimeMinutes(report, DateTimeOffset.UtcNow);
        AddNotifications(report, "restored", $"✅ Роботу відновлено\nЗаявка №{report.Id}\n{report.Machine} · {report.Site}\nНесправність: {report.Fault}\nТип заявки: {RequestTypeLabel(report.RequestType)}\nЧас простою машини: {duration} хв\nЧас простою процесу: {ProcessDowntimeMinutes(report, DateTimeOffset.UtcNow)} хв\nЧас ремонту: {(report.RepairStartedAt is null ? "не фіксувався" : FormatDuration(RepairSeconds(report, DateTimeOffset.UtcNow)!.Value))}\nВідновлення зафіксував: {user.Name} (ID {user.Id})");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) {
            db.ChangeTracker.Clear();
            throw new RuleException("Стан заявок змінився. Оновіть журнал і повторіть завершення ремонту.", 409);
        }
        return report;
    }

    public async Task<Breakdown> StartRepairAsync(int id, AppUser user, CancellationToken ct)
    {
        var report = await db.Breakdowns.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new RuleException("Заявку не знайдено.", 404);
        if (report.RestoredAt is not null) throw new RuleException("Заявку вже завершено.", 409);
        if (!report.RequiresRepairStart) throw new RuleException("Для цієї служби початок ремонту не фіксується.");
        if (report.RepairStartedAt is not null) return report;
        report.RepairStartedAt = DateTimeOffset.UtcNow;
        report.RepairStartedByUserId = user.Id;
        report.Version = Guid.NewGuid();
        var notifications = AddNotifications(report, "repairStarted", $"🔧 Почато ремонт\nЗаявка №{report.Id}\n{Summary(report)}\nПрацівник прибув. Ремонт розпочав: {user.Name}");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            foreach (var notification in notifications) db.Entry(notification).State = EntityState.Detached;
            await db.Entry(report).ReloadAsync(ct);
            if (report.DeletedAt is not null) throw new RuleException("Заявку видалено адміністратором.", 404);
            if (report.RepairStartedAt is null) throw;
        }
        return report;
    }

    private List<Notification> AddNotifications(Breakdown report, string kind, string text)
    {
        var targets = new List<long>();
        if (kind != "repairStarted") targets.Add(report.TelegramUserId);
        var group = config.GetValue<long>("Telegram:GroupChatId");
        if (group < 0) targets.Add(group);
        var items = targets.Distinct().Select(chat => new Notification {
            Breakdown = report, ChatId = chat, Kind = kind,
            Text = chat > 0 ? (kind == "new" ? "Заявка прийнята." : "Роботу відновлено.") : text,
            SentAt = config["Telegram:Mode"] == "Demo" ? DateTimeOffset.UtcNow : null
        }).ToList();
        db.Notifications.AddRange(items);
        return items;
    }

    private static string Summary(Breakdown report) => $"Тип заявки: {RequestTypeLabel(report.RequestType)}\n{report.Site} · зміна {report.Shift}\n{report.Machine}\n{report.Part} / {report.Node}\nНесправність: {report.Fault}\nПрацівник: {report.Person}\nРемонт: {report.RepairMethod}" +
        (report.HasReserveApollo is bool reserve ? $"\nРезервне APOLLO: {(reserve ? "Так" : "Ні")}" : "") +
        (report.Description.Length > 0 ? $"\nОпис: {report.Description[..Math.Min(report.Description.Length, 700)]}" : "");

    public static long DowntimeSeconds(Breakdown report, DateTimeOffset now) => report.RequestType == Productivity ? 0 : Math.Max(0, (long)((report.RestoredAt ?? now) - report.CreatedAt).TotalSeconds);
    public static string RequestTypeLabel(string type) => type == Productivity ? "Зниження продуктивності" : "Аварійна";
    public static long MachineDowntimeMinutes(Breakdown report, DateTimeOffset now) => report.RequestType == Productivity ? 0 : DowntimeMinutes(DowntimeSeconds(report, now));
    public static long ProcessDowntimeSeconds(Breakdown report, DateTimeOffset now) => report.RequestType == Productivity ? 0 : report.HasReserveApollo == true ? 1200 :
        Math.Max(0, (long)((report.ProcessRestoredAt ?? now) - report.CreatedAt).TotalSeconds);
    public static long ProcessDowntimeMinutes(Breakdown report, DateTimeOffset now) => report.RequestType == Productivity ? 0 : report.HasReserveApollo == true ? 20 : DowntimeMinutes(ProcessDowntimeSeconds(report, now));
    public static long DowntimeMinutes(long seconds) => Math.Max(10, (long)Math.Round(seconds / 60m, 0, MidpointRounding.AwayFromZero));
    public static string FormatDowntime(long seconds) => $"{DowntimeMinutes(seconds)} хв";
    public static long? RepairSeconds(Breakdown report, DateTimeOffset now) => report.RepairStartedAt is null ? null : Math.Max(0, (long)((report.RestoredAt ?? now) - report.RepairStartedAt.Value).TotalSeconds);
    public static string FormatDuration(long seconds) => seconds < 60 ? "менше 1 хв" : $"{seconds / 60} хв";
}
