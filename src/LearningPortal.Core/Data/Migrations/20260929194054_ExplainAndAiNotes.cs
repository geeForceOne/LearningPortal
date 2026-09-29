using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearningPortal.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExplainAndAiNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAiWritten",
                table: "Materials",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ExplainSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    TopicId = table.Column<int>(type: "INTEGER", nullable: false),
                    SectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Concept = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Explanation = table.Column<string>(type: "TEXT", nullable: true),
                    QuestionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    RepliesJson = table.Column<string>(type: "TEXT", nullable: true),
                    Score = table.Column<int>(type: "INTEGER", nullable: true),
                    Feedback = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExplainSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExplainSessions_Topics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "Topics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExplainSessions_TopicId",
                table: "ExplainSessions",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_ExplainSessions_UserId_TopicId",
                table: "ExplainSessions",
                columns: new[] { "UserId", "TopicId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExplainSessions");

            migrationBuilder.DropColumn(
                name: "IsAiWritten",
                table: "Materials");
        }
    }
}
