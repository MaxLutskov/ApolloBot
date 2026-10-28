using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApolloBreakdowns.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreKyivTimeOffsets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConvertOffsets(migrationBuilder, "FLE Standard Time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConvertOffsets(migrationBuilder, "UTC");
        }

        private static void ConvertOffsets(MigrationBuilder migrationBuilder, string zone)
        {
            // Input columns are datetimeoffset: AT TIME ZONE preserves their UTC instant.
            var tables = new (string Table, string[] Columns)[] {
                ("Breakdowns", new[] { "CreatedAt", "RepairStartedAt", "RestoredAt", "DeletedAt" }),
                ("Users", new[] { "FirstSeenAt", "LastSeenAt" }),
                ("Notifications", new[] { "NextAttemptAt", "SentAt", "CancelledAt" }),
                ("Audit", new[] { "CreatedAt" }),
                ("Reminders", new[] { "CreatedAt" }),
                ("ReminderDeliveries", new[] { "NextAttemptAt", "SentAt" })
            };
            foreach (var (table, columns) in tables)
            {
                var assignments = string.Join(", ", System.Linq.Enumerable.Select(columns, column =>
                    $"[{column}] = CASE WHEN [{column}] IS NULL THEN NULL ELSE [{column}] AT TIME ZONE N'{zone}' END"));
                var changed = string.Join(" OR ", System.Linq.Enumerable.Select(columns, column =>
                    $"DATEPART(TZOFFSET, [{column}]) <> DATEPART(TZOFFSET, [{column}] AT TIME ZONE N'{zone}')"));
                migrationBuilder.Sql($"UPDATE [apollo].[{table}] SET {assignments} WHERE {changed};");
            }
        }
    }
}
