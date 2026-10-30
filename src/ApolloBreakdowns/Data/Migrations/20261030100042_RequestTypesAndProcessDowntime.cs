using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApolloBreakdowns.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequestTypesAndProcessDowntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProcessRestoredAt",
                schema: "apollo",
                table: "Breakdowns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProcessRestoredByBreakdownId",
                schema: "apollo",
                table: "Breakdowns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestType",
                schema: "apollo",
                table: "Breakdowns",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "emergency");

            // Existing reports are emergency reports. Reconstruct the first actual
            // recovery after each outage began, including repairs on other sites.
            migrationBuilder.Sql("""
                UPDATE outage SET
                    [ProcessRestoredAt] = recovery.[RestoredAt],
                    [ProcessRestoredByBreakdownId] = recovery.[Id]
                FROM [apollo].[Breakdowns] AS outage
                CROSS APPLY (
                    SELECT TOP (1) restored.[Id], restored.[RestoredAt]
                    FROM [apollo].[Breakdowns] AS restored
                    WHERE restored.[RestoredAt] >= outage.[CreatedAt]
                    ORDER BY restored.[RestoredAt], restored.[Id]
                ) AS recovery
                WHERE outage.[HasReserveApollo] = 0 OR outage.[HasReserveApollo] IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Breakdowns_RequestType_ProcessRestoredAt",
                schema: "apollo",
                table: "Breakdowns",
                columns: new[] { "RequestType", "ProcessRestoredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Breakdowns_RequestType_ProcessRestoredAt",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "ProcessRestoredAt",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "ProcessRestoredByBreakdownId",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "RequestType",
                schema: "apollo",
                table: "Breakdowns");
        }
    }
}
