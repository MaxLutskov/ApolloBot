using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ApolloBreakdowns.Data.Migrations;

[DbContext(typeof(ApolloDbContext))]
[Migration("20261016120000_CorrectSiteList")]
public sealed class CorrectSiteList : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Update existing databases once. Keep historical report snapshots and
        // disabled catalog rows intact, and preserve subsequent admin edits.
        var allowed = Enumerable.Range(1, 14).Concat(new[] { 22, 42, 43, 47, 49 }).ToHashSet();
        var removed = Enumerable.Range(1, 49).Where(n => !allowed.Contains(n));
        var labels = string.Join(", ", removed.Select(n => $"N'ВДВП №{n}'"));
        migrationBuilder.Sql($"""
            UPDATE [apollo].[Options]
            SET [Active] = 0, [Version] = NEWID()
            WHERE [Kind] = N'site' AND [Active] = 1 AND [Label] IN ({labels});
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not re-enable incorrect sites or overwrite administrators' choices.
    }
}
