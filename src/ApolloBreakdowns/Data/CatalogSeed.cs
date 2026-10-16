using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ApolloBreakdowns.Data;

public static class CatalogSeed
{
    public static async Task ApplyAsync(ApolloDbContext db)
    {
        var state = await db.BotStates.FindAsync(1);
        if (state?.CatalogSeeded == true) return;
        if (await db.Options.AnyAsync()) throw new InvalidOperationException("Catalog exists without seed marker; manual review required.");
        using var stream = typeof(CatalogSeed).Assembly.GetManifestResourceStream("ApolloBreakdowns.Data.catalog.json")!;
        var rows = (await JsonSerializer.DeserializeAsync<List<SeedOption>>(stream))!;
        var options = new Dictionary<string, FieldOption>();
        // Seed in parent-first layers. Migration lock serializes production startup seeding.
        foreach (var kind in new[] { "site", "shift", "person", "machine", "repairMethod", "part", "node", "fault" })
        {
            var layer = rows.Where(x => x.Kind == kind).ToList();
            var entities = layer.Select((x, i) => new FieldOption {
                Kind = x.Kind, Label = x.Label, Parent = x.Parent is null ? null : options[x.Parent],
                SortOrder = i, RequiresDescription = x.RequiresDescription, RequiresReserve = x.RequiresReserve
            }).ToList();
            db.Options.AddRange(entities);
            for (var i = 0; i < layer.Count; i++) options[layer[i].Key] = entities[i];
        }
        state ??= new BotState { Id = 1 };
        state.CatalogSeeded = true;
        if (db.Entry(state).State == EntityState.Detached) db.BotStates.Add(state);
        await db.SaveChangesAsync();
    }

    private sealed record SeedOption(string Key, string Kind, string Label, string? Parent, bool RequiresDescription, bool RequiresReserve);
}
