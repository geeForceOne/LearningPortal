using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearningPortal.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModelChoicesAndExamDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One model per task instead of one per provider. Existing users keep their provider
            // and that provider's model in simple mode; the advanced defaults start from the same.
            migrationBuilder.AddColumn<bool>(
                name: "AdvancedModels", table: "UserSettings", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<string>(
                name: "Model", table: "UserSettings", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "AnalysisProvider", table: "UserSettings", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>(
                name: "AnalysisModel", table: "UserSettings", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "GenerationProvider", table: "UserSettings", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>(
                name: "GenerationModel", table: "UserSettings", type: "TEXT", nullable: true);

            migrationBuilder.Sql("""
                UPDATE UserSettings SET
                    Model = CASE Provider WHEN 0 THEN ClaudeModel ELSE OpenAiModel END,
                    AnalysisProvider = Provider,
                    AnalysisModel = CASE Provider WHEN 0 THEN ClaudeModel ELSE OpenAiModel END,
                    GenerationProvider = Provider,
                    GenerationModel = CASE Provider WHEN 0 THEN ClaudeModel ELSE OpenAiModel END;
                """);

            migrationBuilder.DropColumn(name: "ClaudeModel", table: "UserSettings");
            migrationBuilder.DropColumn(name: "OpenAiModel", table: "UserSettings");

            migrationBuilder.AddColumn<string>(
                name: "GeneratedByModel", table: "Questions", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "OutlineModel", table: "Materials", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "ExcludedMaterialIds", table: "Exams", type: "TEXT", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaudeModel", table: "UserSettings", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "OpenAiModel", table: "UserSettings", type: "TEXT", nullable: true);
            migrationBuilder.Sql("""
                UPDATE UserSettings SET
                    ClaudeModel = CASE Provider WHEN 0 THEN Model ELSE NULL END,
                    OpenAiModel = CASE Provider WHEN 0 THEN NULL ELSE Model END;
                """);

            migrationBuilder.DropColumn(name: "AdvancedModels", table: "UserSettings");
            migrationBuilder.DropColumn(name: "Model", table: "UserSettings");
            migrationBuilder.DropColumn(name: "AnalysisProvider", table: "UserSettings");
            migrationBuilder.DropColumn(name: "AnalysisModel", table: "UserSettings");
            migrationBuilder.DropColumn(name: "GenerationProvider", table: "UserSettings");
            migrationBuilder.DropColumn(name: "GenerationModel", table: "UserSettings");
            migrationBuilder.DropColumn(name: "GeneratedByModel", table: "Questions");
            migrationBuilder.DropColumn(name: "OutlineModel", table: "Materials");
            migrationBuilder.DropColumn(name: "ExcludedMaterialIds", table: "Exams");
        }
    }
}
