using System.Text;
using System.Text.Json;
using ApolloBreakdowns.Data;
using ApolloBreakdowns.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Api;

public sealed record OptionDto(int Id, string Kind, int? ParentId, string Label, bool Active, bool RequiresDescription, bool RequiresReserve, int SortOrder, Guid Version);
public sealed record SaveOption(string Kind, int? ParentId, string Label, bool Active, bool RequiresDescription, bool RequiresReserve, int SortOrder, Guid? Version);

public static class Endpoints
{
    public static AppUser User(HttpContext ctx) => (AppUser)ctx.Items["User"]!;
    public static OptionDto Dto(FieldOption x) => new(x.Id, x.Kind, x.ParentId, x.Label, x.Active, x.RequiresDescription, x.RequiresReserve, x.SortOrder, x.Version);

    public static void MapAppApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/app").RequireRateLimiting("api");
        api.MapGet("/me", (HttpContext ctx, IConfiguration cfg) => Results.Ok(new { user = User(ctx), demo = cfg["Telegram:Mode"] == "Demo" }));
        api.MapGet("/options", async (HttpContext ctx, ApolloDbContext db, CancellationToken ct) => {
            var query = db.Options.AsNoTracking();
            if (!User(ctx).IsAdmin) query = query.Where(x => x.Active);
            return Results.Ok((await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct)).Select(Dto));
        });
        api.MapPost("/breakdowns", async (SubmitBreakdown input, HttpContext ctx, BreakdownService service, CancellationToken ct) => {
            var result = await service.SubmitAsync(input, User(ctx), ct);
            return Results.Ok(new { result.Id, result.RequestId, result.CreatedAt, message = "Заявку успішно збережено" });
        });
        api.MapGet("/breakdowns", async (HttpContext ctx, ApolloDbContext db, int? page, CancellationToken ct) => {
            var q = db.Breakdowns.AsNoTracking();
            if (!User(ctx).IsAdmin) q = q.Where(x => x.TelegramUserId == User(ctx).Id);
            var p = Math.Clamp(page ?? 1, 1, 100000);
            var items = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip((p - 1) * 20).Take(20).ToListAsync(ct);
            return Results.Ok(new { total = await q.CountAsync(ct), page = p, pageSize = 20, serverTime = DateTimeOffset.UtcNow,
                items = items.Select(x => new { x.Id, x.Site, x.Shift, x.Person, x.Machine, x.Part, x.Node, x.Fault, x.Description,
                    x.RepairMethod, x.HasReserveApollo, x.CreatedAt, x.RestoredAt, x.RestoredByUserId,
                    downtimeSeconds = BreakdownService.DowntimeSeconds(x, DateTimeOffset.UtcNow), canRestore = x.RestoredAt is null }) });
        });
        api.MapPost("/breakdowns/{id:int}/restore", async (int id, HttpContext ctx, BreakdownService service, CancellationToken ct) => {
            var r = await service.RestoreAsync(id, User(ctx), ct);
            return Results.Ok(new { r.Id, r.RestoredAt, downtimeSeconds = BreakdownService.DowntimeSeconds(r, DateTimeOffset.UtcNow) });
        });
        var admin = api.MapGroup("/admin").AddEndpointFilter(async (context, next) => {
            if (!User(context.HttpContext).IsAdmin) return Results.StatusCode(403);
            return await next(context);
        });
        admin.MapPost("/options", async (SaveOption input, HttpContext ctx, ApolloDbContext db, CancellationToken ct) => {
            await ValidateOption(input, null, db, ct);
            var option = new FieldOption { Kind = input.Kind, ParentId = input.ParentId, Label = input.Label.Trim(), Active = input.Active,
                RequiresDescription = input.RequiresDescription, RequiresReserve = input.RequiresReserve, SortOrder = input.SortOrder };
            db.Options.Add(option);
            db.Audit.Add(new AuditEntry { ActorId = User(ctx).Id, Option = option, After = JsonSerializer.Serialize(Dto(option)) });
            await SaveOptions(db, ct);
            return Results.Ok(Dto(option));
        });
        admin.MapPut("/options/{id:int}", async (int id, SaveOption input, HttpContext ctx, ApolloDbContext db, CancellationToken ct) => {
            var option = await db.Options.FindAsync([id], ct);
            if (option is null) return Results.NotFound();
            if (input.Version != option.Version) throw new RuleException("Варіант вже змінено іншим адміністратором. Оновіть довідник.", 409);
            if (input.Kind != option.Kind || input.ParentId != option.ParentId) throw new RuleException("Тип і батьківський елемент змінювати не можна. Створіть новий варіант.");
            await ValidateOption(input, id, db, ct);
            var before = JsonSerializer.Serialize(Dto(option));
            option.Label = input.Label.Trim(); option.Active = input.Active; option.RequiresDescription = input.RequiresDescription;
            option.RequiresReserve = input.RequiresReserve;
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
        admin.MapGet("/export", async (ApolloDbContext db, HttpContext ctx, CancellationToken ct) => {
            ctx.Response.ContentType = "text/csv; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=apollo-breakdowns.csv";
            await ctx.Response.WriteAsync("\uFEFF" + string.Join(';', new[] { "№", "Зареєстровано (UTC)", "Дільниця", "Зміна", "Працівник", "Комбайн", "Частина", "Вузол", "Несправність", "Опис", "Виконання ремонту", "Резервне APOLLO", "Відновлено (UTC)", "Статус", "Простій, сек", "Відновив (Telegram ID)" }.Select(Csv)) + "\r\n", ct);
            await foreach (var r in db.Breakdowns.AsNoTracking().OrderByDescending(x => x.Id).AsAsyncEnumerable().WithCancellation(ct))
                await ctx.Response.WriteAsync(string.Join(';', new[] { r.Id.ToString(), r.CreatedAt.ToString("O"), r.Site, r.Shift, r.Person, r.Machine, r.Part, r.Node, r.Fault, r.Description, r.RepairMethod,
                    r.HasReserveApollo is bool reserve ? (reserve ? "Так" : "Ні") : "", r.RestoredAt?.ToString("O") ?? "", r.RestoredAt is null ? "Простій триває" : "Роботу відновлено",
                    BreakdownService.DowntimeSeconds(r, DateTimeOffset.UtcNow).ToString(), r.RestoredByUserId?.ToString() ?? "" }.Select(Csv)) + "\r\n", ct);
        });
    }

    private static async Task ValidateOption(SaveOption input, int? id, ApolloDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Label) || input.Label.Trim().Length > 300) throw new RuleException("Назва має містити 1–300 символів.");
        var root = new[] { "site", "shift", "person", "machine", "part", "repairMethod" }.Contains(input.Kind);
        if (input.RequiresReserve && input.Kind != "repairMethod") throw new RuleException("Питання про резерв доступне лише для виконання ремонту.");
        if (input.RequiresDescription && input.Kind is not ("part" or "node" or "fault")) throw new RuleException("Обов’язковий опис доступний для частини, вузла або несправності.");
        var parentKind = input.Kind switch { "node" => "part", "fault" => "node", _ => null };
        if (!root && parentKind is null) throw new RuleException("Невідомий тип поля.");
        if (root && input.ParentId is not null) throw new RuleException("Це поле не може мати батьківський елемент.");
        if (parentKind is not null && !await db.Options.AnyAsync(x => x.Id == input.ParentId && x.Kind == parentKind, ct))
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
