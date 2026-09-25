using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearningPortal.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodeTheoryMixAndSeenVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsProgramming",
                table: "Topics",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsCode",
                table: "Questions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CodePercent",
                table: "Exams",
                type: "INTEGER",
                nullable: false,
                defaultValue: 40);

            migrationBuilder.AddColumn<string>(
                name: "SeenVersion",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            // Questions from before the code/theory tag: the ones holding a code block, in the
            // question or an option, were code questions.
            migrationBuilder.Sql("""
                UPDATE Questions SET IsCode = 1
                WHERE instr(Prompt, '```') > 0
                   OR EXISTS (SELECT 1 FROM QuestionOptions o WHERE o.QuestionId = Questions.Id AND instr(o.Text, '```') > 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsProgramming",
                table: "Topics");

            migrationBuilder.DropColumn(
                name: "IsCode",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "CodePercent",
                table: "Exams");

            migrationBuilder.DropColumn(
                name: "SeenVersion",
                table: "AspNetUsers");
        }
    }
}
