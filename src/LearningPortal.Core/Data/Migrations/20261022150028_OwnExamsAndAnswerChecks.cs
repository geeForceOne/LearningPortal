using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearningPortal.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class OwnExamsAndAnswerChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnswerSource",
                table: "Questions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ImportedFrom",
                table: "Questions",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceNote",
                table: "Questions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebSourcesJson",
                table: "Questions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportedFrom",
                table: "Exams",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaperFilePath",
                table: "Exams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingQuestionsJson",
                table: "Exams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AnswerChecks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    TopicId = table.Column<int>(type: "INTEGER", nullable: false),
                    Question = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Answer = table.Column<string>(type: "TEXT", nullable: false),
                    AnswerSource = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceSectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceMaterialId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceLabel = table.Column<string>(type: "TEXT", nullable: true),
                    WebSourcesJson = table.Column<string>(type: "TEXT", nullable: true),
                    SourceNote = table.Column<string>(type: "TEXT", nullable: true),
                    ReferenceAnswer = table.Column<string>(type: "TEXT", nullable: true),
                    Explanation = table.Column<string>(type: "TEXT", nullable: true),
                    Difficulty = table.Column<int>(type: "INTEGER", nullable: false),
                    IsCode = table.Column<bool>(type: "INTEGER", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: true),
                    Feedback = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SavedQuestionId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnswerChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnswerChecks_MaterialSections_SourceSectionId",
                        column: x => x.SourceSectionId,
                        principalTable: "MaterialSections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AnswerChecks_Materials_SourceMaterialId",
                        column: x => x.SourceMaterialId,
                        principalTable: "Materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AnswerChecks_Questions_SavedQuestionId",
                        column: x => x.SavedQuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AnswerChecks_Topics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "Topics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnswerChecks_SavedQuestionId",
                table: "AnswerChecks",
                column: "SavedQuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerChecks_SourceMaterialId",
                table: "AnswerChecks",
                column: "SourceMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerChecks_SourceSectionId",
                table: "AnswerChecks",
                column: "SourceSectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerChecks_TopicId",
                table: "AnswerChecks",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerChecks_UserId_TopicId",
                table: "AnswerChecks",
                columns: new[] { "UserId", "TopicId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnswerChecks");

            migrationBuilder.DropColumn(
                name: "AnswerSource",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "ImportedFrom",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "SourceNote",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "WebSourcesJson",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "ImportedFrom",
                table: "Exams");

            migrationBuilder.DropColumn(
                name: "PaperFilePath",
                table: "Exams");

            migrationBuilder.DropColumn(
                name: "PendingQuestionsJson",
                table: "Exams");
        }
    }
}
