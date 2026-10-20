using ApolloBreakdowns.Api;
using ApolloBreakdowns.Data;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Domain;

public sealed class UserDirectory(ApolloDbContext db, IConfiguration config)
{
    private static readonly SemaphoreSlim RoleChanges = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, SemaphoreSlim> Visits = new();

    public async Task<AppUser> VisitAsync(AppUser visitor, CancellationToken ct)
    {
        var gate = Visits.GetOrAdd(visitor.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await VisitCoreAsync(visitor, ct); }
        finally { gate.Release(); }
    }

    private async Task<AppUser> VisitCoreAsync(AppUser visitor, CancellationToken ct)
    {
        var existing = await db.Users.FindAsync([visitor.Id], ct);
        if (existing is null)
        {
            existing = new BotUser { Id = visitor.Id, Name = visitor.Name[..Math.Min(visitor.Name.Length, 300)], IsAdmin = visitor.IsAdmin, RoleInitialized = true };
            db.Users.Add(existing);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.Entry(existing).State = EntityState.Detached;
                existing = await db.Users.SingleAsync(x => x.Id == visitor.Id, ct);
            }
        }
        else if (existing.LastSeenAt < DateTimeOffset.UtcNow.AddMinutes(-1) || existing.Name != visitor.Name || !existing.RoleInitialized)
        {
            if (!existing.RoleInitialized) { existing.IsAdmin = existing.IsAdmin || visitor.IsAdmin; existing.RoleInitialized = true; }
            existing.Name = visitor.Name[..Math.Min(visitor.Name.Length, 300)];
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            existing.Version = Guid.NewGuid();
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { await db.Entry(existing).ReloadAsync(ct); }
        }
        return new(existing.Id, existing.Name, existing.IsAdmin);
    }

    public Task<AppUser> VisitTelegramAsync(long id, string name, CancellationToken ct) =>
        VisitAsync(new(id, name, (config.GetSection("Admin:TelegramIds").Get<long[]>() ?? []).Contains(id)), ct);

    public async Task SetAdminAsync(long id, bool isAdmin, Guid version, AppUser actor, CancellationToken ct)
    {
        if (!actor.IsAdmin) throw new RuleException("Потрібні права адміністратора.", 403);
        if (actor.Id == id && !isAdmin) throw new RuleException("Не можна забрати права у власного облікового запису.");
        await RoleChanges.WaitAsync(ct);
        try
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
                if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == actor.Id && x.IsAdmin, ct))
                    throw new RuleException("Права адміністратора вже відкликано.", 403);
                var user = await db.Users.FindAsync([id], ct) ?? throw new RuleException("Користувач ще не заходив у бот.", 404);
                await db.Entry(user).ReloadAsync(ct);
                if (version != user.Version) throw new RuleException("Дані користувача змінилися. Оновіть список.", 409);
                user.IsAdmin = isAdmin;
                user.RoleInitialized = true;
                user.Version = Guid.NewGuid();
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateConcurrencyException) { throw new RuleException("Права вже змінено. Оновіть список.", 409); }
                if (transaction is not null) await transaction.CommitAsync(ct);
            });
        }
        finally { RoleChanges.Release(); }
    }
}
