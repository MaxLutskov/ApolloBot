using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApolloBreakdowns.Data.Migrations
{
    /// <inheritdoc />
    public partial class UsersRepairReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Deleted",
                schema: "apollo",
                table: "Options",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsOther",
                schema: "apollo",
                table: "Options",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresRepairStart",
                schema: "apollo",
                table: "Options",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RepairStartedAt",
                schema: "apollo",
                table: "Breakdowns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RepairStartedByUserId",
                schema: "apollo",
                table: "Breakdowns",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresRepairStart",
                schema: "apollo",
                table: "Breakdowns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Reminders",
                schema: "apollo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Time = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Deleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reminders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "apollo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IsAdmin = table.Column<bool>(type: "bit", nullable: false),
                    RoleInitialized = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReminderDeliveries",
                schema: "apollo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScheduleId = table.Column<int>(type: "int", nullable: false),
                    LocalDate = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReminderDeliveries_Reminders_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "apollo",
                        principalTable: "Reminders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(@"
UPDATE [apollo].[Notifications] SET [Text] = CASE WHEN [Kind] = N'new' THEN N'Заявка прийнята.' ELSE N'Роботу відновлено.' END
WHERE [ChatId] > 0 AND [SentAt] IS NULL;
UPDATE [apollo].[Options] SET [RequiresRepairStart] = 1
WHERE [Kind] = N'repairMethod' AND ([Label] LIKE N'%РМД%' OR [Label] LIKE N'%АСКВ%');
UPDATE [apollo].[Breakdowns] SET [RequiresRepairStart] = 1
WHERE [RestoredAt] IS NULL AND ([RepairMethod] LIKE N'%РМД%' OR [RepairMethod] LIKE N'%АСКВ%');
WITH KnownUsers AS (
 SELECT [TelegramUserId], [TelegramName],
 MIN([CreatedAt]) OVER (PARTITION BY [TelegramUserId]) AS FirstSeen,
 MAX([CreatedAt]) OVER (PARTITION BY [TelegramUserId]) AS LastSeen,
 ROW_NUMBER() OVER (PARTITION BY [TelegramUserId] ORDER BY [CreatedAt] DESC, [Id] DESC) AS RowNum
 FROM [apollo].[Breakdowns] WHERE [TelegramUserId] > 0
)
INSERT INTO [apollo].[Users] ([Id], [Name], [IsAdmin], [RoleInitialized], [FirstSeenAt], [LastSeenAt], [Version])
SELECT [TelegramUserId], [TelegramName], 0, 0, FirstSeen, LastSeen, NEWID() FROM KnownUsers WHERE RowNum = 1;
");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderDeliveries_ScheduleId_LocalDate",
                schema: "apollo",
                table: "ReminderDeliveries",
                columns: new[] { "ScheduleId", "LocalDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReminderDeliveries",
                schema: "apollo");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "apollo");

            migrationBuilder.DropTable(
                name: "Reminders",
                schema: "apollo");

            migrationBuilder.DropColumn(
                name: "Deleted",
                schema: "apollo",
                table: "Options");

            migrationBuilder.DropColumn(
                name: "IsOther",
                schema: "apollo",
                table: "Options");

            migrationBuilder.DropColumn(
                name: "RequiresRepairStart",
                schema: "apollo",
                table: "Options");

            migrationBuilder.DropColumn(
                name: "RepairStartedAt",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "RepairStartedByUserId",
                schema: "apollo",
                table: "Breakdowns");

            migrationBuilder.DropColumn(
                name: "RequiresRepairStart",
                schema: "apollo",
                table: "Breakdowns");
        }
    }
}
