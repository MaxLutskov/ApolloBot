using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApolloBreakdowns.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminReportDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                schema: "apollo",
                table: "Notifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "apollo",
                table: "Breakdowns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeletedByUserId",
                schema: "apollo",
                table: "Breakdowns",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelledAt",
                schema: "apollo",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                schema: "apollo",
                table: "Breakdowns");
        }
    }
}
