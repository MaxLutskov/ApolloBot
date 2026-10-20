using System.Text;
using System.Text.Json;
using ApolloBreakdowns.Data;
using ApolloBreakdowns.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Api;

public sealed record OptionDto(int Id, string Kind, int? ParentId, string Label, bool Active, bool RequiresDescription, bool RequiresReserve, int SortOrder, Guid Version, bool RequiresRepairStart, bool IsOther);
public sealed record SaveOption(string Kind, int? ParentId, string Label, bool Active, bool RequiresDescription, bool RequiresReserve, int SortOrder, Guid? Version, bool RequiresRepairStart = false);

public static class Endpoints
{
    public static AppUser User(HttpContext ctx) => (AppUser)ctx.Items["User"]!;
    public static OptionDto Dto(FieldOption x) => new(x.Id, x.Kind, x.ParentId, x.Label, x.Active, x.RequiresDescription, x.RequiresReserve, x.SortOrder, x.Version, x.RequiresRepairStart, x.IsOther);

    public static void MapAppApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/app").RequireRateLimiting("api");
        api.MapGet("/me", (HttpContext ctx, IConfiguration cfg, ApolloDbContext db) => Results.Ok(new { user = User(ctx), demo = cfg["Telegram:Mode"] == "Demo", temporaryStorage = db.Database.IsInMemory() }));
        api.MapGet("/options", async (HttpContext ctx, ApolloDbContext db, CancellationToken ct) => {
            var query = db.Options.AsNoTracking().Where(x => !x.Deleted);
            if (!User(ctx).IsAdmin) query = query.Where(x => x.Active);
            return Results.Ok((await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct)).Select(Dto));
        });
        api.MapPost("/breakdowns", async (SubmitBreakdown input, HttpContext ctx, BreakdownService service, CancellationToken ct) => {
            var result = await service.SubmitAsync(input, User(ctx), ct);
            return Results.Ok(new { result.Id, result.RequestId, result.CreatedAt, message = "Заявку успішно збережено" });
        });
        api.MapGet("/breakdowns", async (HttpContext ctx, ApolloDbContext db, int? page, CancellationToken ct) => {
            var q = db.Breakdowns.AsNoTracking();
            var p = Math.Clamp(page ?? 1, 1, 100000);
            var items = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip((p - 1) * 20).Take(20).ToListAsync(ct);
            return Results.Ok(new { total = await q.CountAsync(ct), page = p, pageSize = 20, serverTime = DateTimeOffset.UtcNow,
                items = items.Select(x => new { x.Id, x.Site, x.Shift, x.Person, x.Machine, x.Part, x.Node, x.Fault, x.Description,
                    x.RepairMethod, x.HasReserveApollo, x.RequiresRepairStart, x.RepairStartedAt, x.RepairStartedByUserId, x.CreatedAt, x.RestoredAt, x.RestoredByUserId,
                    downtimeSeconds = BreakdownService.DowntimeSeconds(x, DateTimeOffset.UtcNow), repairSeconds = BreakdownService.RepairSeconds(x, DateTimeOffset.UtcNow),
                    canStartRepair = x.RestoredAt is null && x.RequiresRepairStart && x.RepairStartedAt is null,
                    canRestore = x.RestoredAt is null && (!x.RequiresRepairStart || x.RepairStartedAt is not null) }) });
        });
        api.MapPost("/breakdowns/{id:int}/restore", async (int id, HttpContext ctx, BreakdownService service, CancellationToken ct) => {
            var r = await service.RestoreAsync(id, User(ctx), ct);
            return Results.Ok(new { r.Id, r.RestoredAt, downtimeSeconds = BreakdownService.DowntimeSeconds(r, DateTimeOffset.UtcNow) });
        });
        api.MapPost("/breakdowns/{id:int}/start-repair", async (int id, HttpContext ctx, BreakdownService service, CancellationToken ct) => {
            var report = await service.StartRepairAsync(id, User(ctx), ct);
            return Results.Ok(new { report.Id, report.RepairStartedAt });
        });
        api.MapGet("/export", async (ApolloDbContext db, CancellationToken ct) => Results.File(await HistoryExport.BuildAsync(db, ct), "text/csv; charset=utf-8", "Apollo-history.csv"));
        api.MapPost("/export/send", async (ApolloDbContext db, HttpContext ctx, Telegram.TelegramClient telegram, IConfiguration cfg, CancellationToken ct) => {
            if (cfg["Telegram:Mode"] == "Demo") throw new RuleException("У тестовому режимі використайте завантаження CSV.");
            try { await telegram.SendDocumentAsync(User(ctx).Id, await HistoryExport.BuildAsync(db, ct), ct); }
            catch (Telegram.TelegramException) { throw new RuleException("Не вдалося надіслати CSV. Відкрийте особистий чат із ботом, натисніть Start і повторіть спробу.", 502); }
            return Results.Ok(new { message = "CSV надіслано в особистий чат із ботом." });
        });
        var admin = api.MapGroup("/admin").AddEndpointFilter(async (context, next) => {
            if (!User(context.HttpContext).IsAdmin) return Results.StatusCode(403);
            return await next(context);
        });
        admin.MapPost("/options", async (SaveOption input, HttpContext ctx, ApolloDbContext db, CancellationToken ct) => {
            await ValidateOption(input, null, db, ct);
            var option = new FieldOption { Kind = input.Kind, ParentId = input.ParentId, Label = input.Label.Trim(), Active = input.Active,
                RequiresDescription = input.RequiresDescription, RequiresReserve = input.RequiresReserve, RequiresRepairStart = input.RequiresRepairStart, SortOrder = input.SortOrder };
            db.Options.Add(option);
            db.Audit.Add(new AuditEntry { ActorId = User(ctx).Id, Option = option, After = JsonSerializer.Serialize(Dto(option)) });
            await SaveOptions(db, ct);
            await CatalogSeed.EnsureOtherOptionsAsync(db);
            return Results.Ok(Dto(option));
        });
        admin.MapPut("/options/{id:int}", async (int id, SaveOption input, HttpContext ctx, ApolloDbContext db, CancellationToken ct) => {
            var option = await db.Options.FindAsync([id], ct);
            if (option is null || option.Deleted) return Results.NotFound();
            if (input.Version != option.Version) throw new RuleException("Варіант вже змінено іншим адміністратором. Оновіть довідник.", 409);
            if (input.Kind != option.Kind || input.ParentId != option.ParentId) throw new RuleException("Тип і батьківський елемент змінювати не можна. Створіть новий варіант.");
            await ValidateOption(input, id, db, ct);
            var before = JsonSerializer.Serialize(Dto(option));
            if (option.IsOther && (input.Label.Trim() != option.Label || !input.Active)) throw new RuleException("Системний пункт «Інше» має залишатися доступним.");
            option.Label = input.Label.Trim(); option.Active = input.Active; option.RequiresDescription = option.IsOther && option.Kind is "part" or "node" or "fault" || input.RequiresDescription;
            option.RequiresReserve = input.RequiresReserve;
            option.RequiresRepairStart = input.RequiresRepairStart;
            option.SortOrder = input.SortOrder; option.Version = Guid.NewGuid();
            db.Audit.Add(new AuditEntry { ActorId = User(ctx).Id, OptionId = id, Before = before, After = JsonSerializer.Serialize(Dto(option)) });
            await SaveOptions(db, ct);
            return Results.Ok(Dto(option));
        });
        admin.MapGet("/notifications", async (ApolloDbContext db, CancellationToken ct) => Results.Ok(new {
            pending = await db.Notifications.CountAsync(x => x.SentAt == null, ct),
            delayed = await db.Notifications.CountAsync(x => x.SentAt == null && x.Attempts > 0, ct)
        }));
        admin.MapGet("/audit", async (ApolloDbContext db, CancellationToken ct) => Results.Ok(await db.Audit.AsNoTracking().OrderByDescending(x => x.Id).Take(100).ToListAsync(ct)));
        admin.MapGet("/export", async (ApolloDbContext db, CancellationToken ct) => Results.File(await HistoryExport.BuildAsync(db, ct), "text/csv; charset=utf-8", "Apollo-history.csv"));
        admin.MapDelete("/options/{id:int}", async (int id, Guid version, ApolloDbContext db, HttpContext ctx, CancellationToken ct) => {
            var all = await db.Options.Where(x => !x.Deleted).ToListAsync(ct);
            var option = all.SingleOrDefault(x => x.Id == id) ?? throw new RuleException("Варіант не знайдено.", 404);
            if (option.IsOther) throw new RuleException("Системний пункт «Інше» видаляти не можна.");
            if (option.Version != version) throw new RuleException("Варіант змінився. Оновіть довідник.", 409);
            var ids = new HashSet<int> { id };
            while (true) {
                var children = all.Where(x => x.ParentId is int parent && ids.Contains(parent) && !ids.Contains(x.Id)).ToList();
                if (children.Count == 0) break;
                foreach (var child in children) ids.Add(child.Id);
            }
            foreach (var item in all.Where(x => ids.Contains(x.Id))) {
                var before = JsonSerializer.Serialize(Dto(item));
                item.Deleted = true; item.Active = false; item.Version = Guid.NewGuid();
                db.Audit.Add(new AuditEntry { ActorId = User(ctx).Id, OptionId = item.Id, Before = before, After = "deleted" });
            }
            await SaveOptions(db, ct);
            return Results.Ok(new { deleted = ids.Count });
        });
        admin.MapGet("/users", async (ApolloDbContext db, CancellationToken ct) => Results.Ok(await db.Users.AsNoTracking().OrderByDescending(x => x.LastSeenAt).ToListAsync(ct)));
        admin.MapPut("/users/{id:long}/admin", async (long id, AdminRole input, HttpContext ctx, UserDirectory directory, CancellationToken ct) => {
            await directory.SetAdminAsync(id, input.IsAdmin, input.Version, User(ctx), ct);
            return Results.Ok(new { message = "Права оновлено." });
        });
        admin.MapGet("/reminders", async (ApolloDbContext db, CancellationToken ct) => Results.Ok(await db.Reminders.Where(x => !x.Deleted).OrderBy(x => x.Time).ToListAsync(ct)));
        admin.MapPost("/reminders", async (SaveReminder input, ApolloDbContext db, CancellationToken ct) => {
            ValidateReminder(input);
            if (await db.Reminders.AnyAsync(x => !x.Deleted && x.Time == input.Time, ct)) throw new RuleException("Цей час уже є у розкладі.", 409);
            var reminder = new ReminderSchedule { Time = input.Time, Active = input.Active };
            db.Reminders.Add(reminder); await db.SaveChangesAsync(ct);
            return Results.Ok(reminder);
        });
        admin.MapPut("/reminders/{id:int}", async (int id, SaveReminder input, ApolloDbContext db, CancellationToken ct) => {
            ValidateReminder(input);
            var reminder = await db.Reminders.FindAsync([id], ct) ?? throw new RuleException("Розсилку не знайдено.", 404);
            if (reminder.Deleted) throw new RuleException("Розсилку видалено.", 404);
            if (reminder.Version != input.Version) throw new RuleException("Розклад змінився. Оновіть сторінку.", 409);
            if (await db.Reminders.AnyAsync(x => x.Id != id && !x.Deleted && x.Time == input.Time, ct)) throw new RuleException("Цей час уже є у розкладі.", 409);
            reminder.Time = input.Time; reminder.Active = input.Active; reminder.Version = Guid.NewGuid();
            await SaveReminderChanges(db, ct); return Results.Ok(reminder);
        });
        admin.MapDelete("/reminders/{id:int}", async (int id, Guid version, ApolloDbContext db, CancellationToken ct) => {
            var reminder = await db.Reminders.FindAsync([id], ct) ?? throw new RuleException("Розсилку не знайдено.", 404);
            if (reminder.Version != version) throw new RuleException("Розклад змінився. Оновіть сторінку.", 409);
            reminder.Deleted = true; reminder.Active = false; reminder.Version = Guid.NewGuid();
            await SaveReminderChanges(db, ct); return Results.Ok(new { deleted = true });
        });
    }

    public sealed record AdminRole(bool IsAdmin, Guid Version);
    public sealed record SaveReminder(string Time, bool Active, Guid? Version);
    private static void ValidateReminder(SaveReminder input) {
        if (!ReminderService.ValidTime(input.Time)) throw new RuleException("Вкажіть час у форматі ГГ:ХХ, наприклад 07:00 (Київ).");
    }
    private static async Task SaveReminderChanges(ApolloDbContext db, CancellationToken ct) {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new RuleException("Розклад змінився. Оновіть сторінку.", 409); }
    }

    private static async Task ValidateOption(SaveOption input, int? id, ApolloDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Label) || input.Label.Trim().Length > 300) throw new RuleException("Назва має містити 1–300 символів.");
        var root = new[] { "site", "shift", "person", "machine", "part", "repairMethod" }.Contains(input.Kind);
        if (input.RequiresRepairStart && input.Kind != "repairMethod") throw new RuleException("Початок ремонту налаштовується лише для виконавців ремонту.");
        if (input.RequiresReserve && input.Kind != "repairMethod") throw new RuleException("Питання про резерв доступне лише для виконання ремонту.");
        if (input.RequiresDescription && input.Kind is not ("part" or "node" or "fault")) throw new RuleException("Обов’язковий опис доступний для частини, вузла або несправності.");
        var parentKind = input.Kind switch { "node" => "part", "fault" => "node", _ => null };
        if (!root && parentKind is null) throw new RuleException("Невідомий тип поля.");
        if (root && input.ParentId is not null) throw new RuleException("Це поле не може мати батьківський елемент.");
        if (parentKind is not null && !await db.Options.AnyAsync(x => x.Id == input.ParentId && x.Kind == parentKind && !x.Deleted, ct))
            throw new RuleException("Оберіть правильний батьківський елемент.");
        if (await db.Options.AnyAsync(x => x.Id != id && x.Kind == input.Kind && x.ParentId == input.ParentId && x.Label == input.Label.Trim(), ct))
            throw new RuleException("Варіант з такою назвою вже існує.", 409);
    }

    private static async Task SaveOptions(ApolloDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new RuleException("Довідник вже змінився. Оновіть сторінку.", 409); }
        catch (DbUpdateException) { throw new RuleException("Не вдалося зберегти варіант. Перевірте, чи немає дубліката.", 409); }
    }

    public static string Csv(string value)
    {
        // Prevent Excel formula injection, including leading whitespace/control characters.
        var first = value.TrimStart();
        if (first.Length > 0 && "=+-@".Contains(first[0]) || value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
