using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The authored companion to every Problem Report narrative field.
    ///
    /// Purely additive, and deliberately without a backfill. Each column defaults to empty, which means
    /// "nothing structured was authored" — and for every existing record that is the truth: the plain
    /// column holds everything anybody ever wrote in that field. Manufacturing rich content from the plain
    /// text would claim the author had structured it when they had not, and the reader already falls back
    /// to the plain value when the rich one is empty.
    /// </summary>
    public partial class AddProblemReportAuthoredNarrative : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnalysisRich",
                table: "problem_reports",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContainmentRich",
                table: "problem_reports",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CorrectiveActionRich",
                table: "problem_reports",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EffectsRich",
                table: "problem_reports",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RootCauseRich",
                table: "problem_reports",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SystemAircraftImpactRich",
                table: "problem_reports",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WorkaroundRich",
                table: "problem_reports",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnalysisRich",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "ContainmentRich",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "CorrectiveActionRich",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "EffectsRich",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "RootCauseRich",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "SystemAircraftImpactRich",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "WorkaroundRich",
                table: "problem_reports");
        }
    }
}
