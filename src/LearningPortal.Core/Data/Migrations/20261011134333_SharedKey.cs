using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearningPortal.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharedKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseSharedKey",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SharedAllowedModels",
                table: "SiteSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedClaudeKeyProtected",
                table: "SiteSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedOpenAiKeyProtected",
                table: "SiteSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SharedKeyGrants",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    BalanceMicros = table.Column<long>(type: "INTEGER", nullable: true),
                    Monthly = table.Column<bool>(type: "INTEGER", nullable: false),
                    BalanceSpentMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    MonthSpentMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalSpentMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedKeyGrants", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_SharedKeyGrants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharedKeyGrants");

            migrationBuilder.DropColumn(
                name: "UseSharedKey",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "SharedAllowedModels",
                table: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "SharedClaudeKeyProtected",
                table: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "SharedOpenAiKeyProtected",
                table: "SiteSettings");
        }
    }
}
