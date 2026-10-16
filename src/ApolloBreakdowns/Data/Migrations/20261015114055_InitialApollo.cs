using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApolloBreakdowns.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialApollo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "apollo");

            migrationBuilder.CreateTable(
                name: "BotStates",
                schema: "apollo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Offset = table.Column<long>(type: "bigint", nullable: false),
                    CatalogSeeded = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Breakdowns",
                schema: "apollo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    TelegramName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Site = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Shift = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Person = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Machine = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Part = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Node = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Fault = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    RepairMethod = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    HasReserveApollo = table.Column<bool>(type: "bit", nullable: true),
                    RestoredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RestoredByUserId = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Breakdowns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Options",
                schema: "apollo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Label = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    RequiresDescription = table.Column<bool>(type: "bit", nullable: false),
                    RequiresReserve = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Options", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Options_Options_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "apollo",
                        principalTable: "Options",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "apollo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BreakdownId = table.Column<int>(type: "int", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Breakdowns_BreakdownId",
                        column: x => x.BreakdownId,
                        principalSchema: "apollo",
                        principalTable: "Breakdowns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Audit",
                schema: "apollo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    OptionId = table.Column<int>(type: "int", nullable: false),
                    Before = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    After = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Audit_Options_OptionId",
                        column: x => x.OptionId,
                        principalSchema: "apollo",
                        principalTable: "Options",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_OptionId",
                schema: "apollo",
                table: "Audit",
                column: "OptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Breakdowns_TelegramUserId_CreatedAt",
                schema: "apollo",
                table: "Breakdowns",
                columns: new[] { "TelegramUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Breakdowns_TelegramUserId_RequestId",
                schema: "apollo",
                table: "Breakdowns",
                columns: new[] { "TelegramUserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_BreakdownId_ChatId_Kind",
                schema: "apollo",
                table: "Notifications",
                columns: new[] { "BreakdownId", "ChatId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_SentAt_NextAttemptAt",
                schema: "apollo",
                table: "Notifications",
                columns: new[] { "SentAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Options_Kind_Label",
                schema: "apollo",
                table: "Options",
                columns: new[] { "Kind", "Label" },
                unique: true,
                filter: "[ParentId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Options_Kind_ParentId_Label",
                schema: "apollo",
                table: "Options",
                columns: new[] { "Kind", "ParentId", "Label" },
                unique: true,
                filter: "[ParentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Options_ParentId",
                schema: "apollo",
                table: "Options",
                column: "ParentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Audit",
                schema: "apollo");

            migrationBuilder.DropTable(
                name: "BotStates",
                schema: "apollo");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "apollo");

            migrationBuilder.DropTable(
                name: "Options",
                schema: "apollo");

            migrationBuilder.DropTable(
                name: "Breakdowns",
                schema: "apollo");
        }
    }
}
