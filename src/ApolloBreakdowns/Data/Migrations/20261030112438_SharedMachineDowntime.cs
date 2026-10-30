using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApolloBreakdowns.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharedMachineDowntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MachineDowntimeGroupId",
                schema: "apollo",
                table: "Breakdowns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MachineDowntimeStartedAt",
                schema: "apollo",
                table: "Breakdowns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MachineKey",
                schema: "apollo",
                table: "Breakdowns",
                type: "nvarchar(350)",
                maxLength: 350,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MachineRestoredAt",
                schema: "apollo",
                table: "Breakdowns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MachineRestoredByBreakdownId",
                schema: "apollo",
                table: "Breakdowns",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE report SET [MachineKey] = CASE WHEN optionRow.[Id] IS NOT NULL
                    THEN N'option:' + CONVERT(nvarchar(20), optionRow.[Id]) ELSE N'legacy:' + report.[Machine] END
                FROM [apollo].[Breakdowns] AS report
                OUTER APPLY (SELECT TOP (1) [Id] FROM [apollo].[Options]
                    WHERE [Kind] = N'machine' AND [Label] = report.[Machine] ORDER BY [Id]) AS optionRow;

                -- Running maximum, rather than LAG, handles nested and chained overlaps.
                ;WITH intervals AS (
                    SELECT [Id], [IsTest], [MachineKey], [CreatedAt], [RestoredAt],
                        MAX(COALESCE([RestoredAt], CONVERT(datetimeoffset, '9999-12-30T00:00:00+00:00')))
                            OVER (PARTITION BY [IsTest], [MachineKey] ORDER BY [CreatedAt], [Id]
                                  ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS previousEnd
                    FROM [apollo].[Breakdowns] WHERE [RequestType] = N'emergency' AND [DeletedAt] IS NULL
                ), boundaries AS (
                    SELECT *, CASE WHEN previousEnd IS NULL OR previousEnd < [CreatedAt] THEN 1 ELSE 0 END AS newGroup
                    FROM intervals
                )
                SELECT [Id], [IsTest], [MachineKey], [CreatedAt], [RestoredAt],
                    SUM(newGroup) OVER (PARTITION BY [IsTest], [MachineKey] ORDER BY [CreatedAt], [Id]
                                        ROWS UNBOUNDED PRECEDING) AS groupNumber
                INTO #machineIntervals FROM boundaries;

                SELECT [IsTest], [MachineKey], groupNumber, NEWID() AS groupId,
                    MIN([CreatedAt]) AS startedAt,
                    CASE WHEN MAX(CASE WHEN [RestoredAt] IS NULL THEN 1 ELSE 0 END) = 1
                         THEN NULL ELSE MAX([RestoredAt]) END AS restoredAt
                INTO #machineGroups FROM #machineIntervals GROUP BY [IsTest], [MachineKey], groupNumber;

                UPDATE report SET [MachineDowntimeGroupId] = episode.groupId,
                    [MachineDowntimeStartedAt] = episode.startedAt, [MachineRestoredAt] = episode.restoredAt,
                    [MachineRestoredByBreakdownId] = completed.[Id], [Version] = NEWID()
                FROM [apollo].[Breakdowns] AS report
                JOIN #machineIntervals AS member ON member.[Id] = report.[Id]
                JOIN #machineGroups AS episode ON episode.[IsTest] = member.[IsTest]
                    AND episode.[MachineKey] = member.[MachineKey] AND episode.groupNumber = member.groupNumber
                OUTER APPLY (SELECT TOP (1) ending.[Id] FROM #machineIntervals AS ending
                    WHERE ending.[IsTest] = episode.[IsTest] AND ending.[MachineKey] = episode.[MachineKey]
                        AND ending.groupNumber = episode.groupNumber AND ending.[RestoredAt] = episode.restoredAt
                    ORDER BY ending.[Id] DESC) AS completed;

                -- A partial repair is not a process recovery event. Rebuild prior
                -- process end markers using only genuinely available machines.
                SELECT [IsTest], [MachineRestoredAt] AS restoredAt, [MachineRestoredByBreakdownId] AS reportId
                INTO #machineRecoveries FROM [apollo].[Breakdowns]
                WHERE [DeletedAt] IS NULL AND [MachineRestoredAt] IS NOT NULL
                GROUP BY [IsTest], [MachineRestoredAt], [MachineRestoredByBreakdownId];

                INSERT INTO #machineRecoveries ([IsTest], restoredAt, reportId)
                SELECT repaired.[IsTest], repaired.[RestoredAt], repaired.[Id]
                FROM [apollo].[Breakdowns] AS repaired
                WHERE repaired.[DeletedAt] IS NULL AND repaired.[RequestType] = N'productivity' AND repaired.[RestoredAt] IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM #machineGroups AS blocked
                        WHERE blocked.[IsTest] = repaired.[IsTest] AND blocked.[MachineKey] = repaired.[MachineKey]
                            AND blocked.startedAt <= repaired.[RestoredAt]
                            AND (blocked.restoredAt IS NULL OR blocked.restoredAt > repaired.[RestoredAt]));

                UPDATE outage SET [ProcessRestoredAt] = recovered.restoredAt,
                    [ProcessRestoredByBreakdownId] = recovered.reportId
                FROM [apollo].[Breakdowns] AS outage
                OUTER APPLY (SELECT TOP (1) * FROM #machineRecoveries AS available
                    WHERE available.[IsTest] = outage.[IsTest] AND available.restoredAt >= outage.[CreatedAt]
                    ORDER BY available.restoredAt, available.reportId) AS recovered
                WHERE outage.[DeletedAt] IS NULL AND outage.[RequestType] = N'emergency'
                    AND (outage.[HasReserveApollo] = 0 OR outage.[HasReserveApollo] IS NULL);

                DROP TABLE #machineRecoveries;
                DROP TABLE #machineGroups;
                DROP TABLE #machineIntervals;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Breakdowns_IsTest_MachineKey_RestoredAt",
                schema: "apollo",
                table: "Breakdowns",
                columns: new[] { "IsTest", "MachineKey", "RestoredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Breakdowns_MachineDowntimeGroupId",
                schema: "apollo",
                table: "Breakdowns",
                column: "MachineDowntimeGroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Breakdowns_IsTest_MachineKey_RestoredAt",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropIndex(
                name: "IX_Breakdowns_MachineDowntimeGroupId",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "MachineDowntimeGroupId",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "MachineDowntimeStartedAt",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "MachineKey",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "MachineRestoredAt",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "MachineRestoredByBreakdownId",
                schema: "apollo",
                table: "Breakdowns");
        }
    }
}
