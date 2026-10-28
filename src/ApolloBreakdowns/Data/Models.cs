using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Data;

public sealed class FieldOption
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public int? ParentId { get; set; }
    public FieldOption? Parent { get; set; }
    public string Label { get; set; } = "";
    public bool Active { get; set; } = true;
    public bool RequiresDescription { get; set; }
    public bool RequiresReserve { get; set; }
    public bool RequiresRepairStart { get; set; }
    public bool IsOther { get; set; }
    public bool Deleted { get; set; }
    public int SortOrder { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class Breakdown
{
    public int Id { get; set; }
    public Guid RequestId { get; set; }
    public string Fingerprint { get; set; } = "";
    public long TelegramUserId { get; set; }
    public string TelegramName { get; set; } = "";
    // Immutable labels protect history when the catalog is renamed or disabled.
    public string Site { get; set; } = "";
    public string Shift { get; set; } = "";
    public string Person { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Part { get; set; } = "";
    public string Node { get; set; } = "";
    public string Fault { get; set; } = "";
    public string Description { get; set; } = "";
    public string RepairMethod { get; set; } = "";
    public bool? HasReserveApollo { get; set; }
    public bool RequiresRepairStart { get; set; }
    public DateTimeOffset? RepairStartedAt { get; set; }
    public long? RepairStartedByUserId { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long? DeletedByUserId { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
    public long? RestoredByUserId { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Notification
{
    public int Id { get; set; }
    public int BreakdownId { get; set; }
    public Breakdown Breakdown { get; set; } = null!;
    public long ChatId { get; set; }
    public string Kind { get; set; } = "new";
    public string Text { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class AuditEntry
{
    public int Id { get; set; }
    public long ActorId { get; set; }
    public int OptionId { get; set; }
    public FieldOption Option { get; set; } = null!;
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BotState
{
    public int Id { get; set; }
    public long Offset { get; set; }
    public bool CatalogSeeded { get; set; }
}

public sealed class BotUser
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool RoleInitialized { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class ReminderSchedule
{
    public int Id { get; set; }
    public string Time { get; set; } = "07:00";
    public bool Active { get; set; } = true;
    public bool Deleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class ReminderDelivery
{
    public int Id { get; set; }
    public int ScheduleId { get; set; }
    public string LocalDate { get; set; } = "";
    public long ChatId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public int Attempts { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class ApolloDbContext(DbContextOptions<ApolloDbContext> options) : DbContext(options)
{
    public const string Schema = "apollo";
    public DbSet<FieldOption> Options => Set<FieldOption>();
    public DbSet<Breakdown> Breakdowns => Set<Breakdown>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<BotState> BotStates => Set<BotState>();

    public DbSet<BotUser> Users => Set<BotUser>();
    public DbSet<ReminderSchedule> Reminders => Set<ReminderSchedule>();
    public DbSet<ReminderDelivery> ReminderDeliveries => Set<ReminderDelivery>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizeTimestampOffsets();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        NormalizeTimestampOffsets();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void NormalizeTimestampOffsets()
    {
        // datetimeoffset retains the Kyiv offset while keeping the same instant for comparisons and durations.
        foreach (var entry in ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified).ToList())
        foreach (var property in entry.Properties)
        {
            if (property.CurrentValue is not DateTimeOffset instant) continue;
            var local = ApplicationTime.InKyiv(instant);
            if (instant.Offset == local.Offset) continue;
            property.CurrentValue = local;
            if (entry.State == EntityState.Modified) property.IsModified = true;
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<BotUser>(e => {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        b.Entity<ReminderSchedule>(e => {
            e.Property(x => x.Time).HasMaxLength(5);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        b.Entity<ReminderDelivery>(e => {
            e.Property(x => x.LocalDate).HasMaxLength(10);
            e.Property(x => x.Text).HasMaxLength(4000);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.ScheduleId, x.LocalDate }).IsUnique();
            e.HasOne<ReminderSchedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<BotState>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<FieldOption>(e => {
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Label).HasMaxLength(300);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.Kind, x.ParentId, x.Label }).IsUnique();
            e.HasIndex(x => new { x.Kind, x.Label }).IsUnique().HasFilter("[ParentId] IS NULL");
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Breakdown>(e => {
            e.HasQueryFilter(x => x.DeletedAt == null);
            e.HasIndex(x => new { x.TelegramUserId, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.TelegramUserId, x.CreatedAt });
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.Property(x => x.Version).IsConcurrencyToken();
            foreach (var name in new[] { "TelegramName", "Site", "Shift", "Person", "Machine", "Part", "Node", "Fault", "RepairMethod" }) e.Property<string>(name).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(4000);
        });
        b.Entity<Notification>(e => {
            e.Property(x => x.Kind).HasMaxLength(20);
            e.HasIndex(x => new { x.BreakdownId, x.ChatId, x.Kind }).IsUnique();
            e.HasIndex(x => new { x.SentAt, x.NextAttemptAt });
            e.Property(x => x.Text).HasMaxLength(4000);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Breakdown).WithMany().HasForeignKey(x => x.BreakdownId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AuditEntry>().HasOne(x => x.Option).WithMany().HasForeignKey(x => x.OptionId).OnDelete(DeleteBehavior.Restrict);
    }
}
