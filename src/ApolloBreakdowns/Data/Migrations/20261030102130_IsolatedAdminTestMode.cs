using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApolloBreakdowns.Data.Migrations
{
    /// <inheritdoc />
    public partial class IsolatedAdminTestMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Breakdowns_TelegramUserId_RequestId",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.AddColumn<bool>(
                name: "IsTest",
                schema: "apollo",
                table: "Reminders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTest",
                schema: "apollo",
                table: "Breakdowns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Breakdowns_IsTest_TelegramUserId_RequestId",
                schema: "apollo",
                table: "Breakdowns",
                columns: new[] { "IsTest", "TelegramUserId", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Breakdowns_IsTest_TelegramUserId_RequestId",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "IsTest",
                schema: "apollo",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "IsTest",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.CreateIndex(
                name: "IX_Breakdowns_TelegramUserId_RequestId",
                schema: "apollo",
                table: "Breakdowns",
                columns: new[] { "TelegramUserId", "RequestId" },
                unique: true);
        }
    }
}
