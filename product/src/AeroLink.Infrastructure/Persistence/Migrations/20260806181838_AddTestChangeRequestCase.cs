using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTestChangeRequestCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Analysis",
                table: "test_change_reviews",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AnalysisRich",
                table: "test_change_reviews",
                type: "text",
                nullable: false,
                defaultValue: "{\"blocks\":[]}");

            migrationBuilder.AddColumn<string>(
                name: "Problem",
                table: "test_change_reviews",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProblemRich",
                table: "test_change_reviews",
                type: "text",
                nullable: false,
                defaultValue: "{\"blocks\":[]}");

            migrationBuilder.AddColumn<string>(
                name: "Solution",
                table: "test_change_reviews",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SolutionRich",
                table: "test_change_reviews",
                type: "text",
                nullable: false,
                defaultValue: "{\"blocks\":[]}");

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "test_change_reviews",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Analysis",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "AnalysisRich",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "Problem",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "ProblemRich",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "Solution",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "SolutionRich",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "test_change_reviews");
        }
    }
}
