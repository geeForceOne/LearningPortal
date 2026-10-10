using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearningPortal.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SectionConcepts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConceptsJson",
                table: "MaterialSections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConceptsJson",
                table: "MaterialSections");
        }
    }
}
