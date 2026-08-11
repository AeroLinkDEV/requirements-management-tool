using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProblemReportRevisionEventEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "problem_report_revisions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "EventSchemaVersion",
                table: "problem_report_revisions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceJson",
                table: "problem_report_revisions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Detail",
                table: "problem_report_revisions");

            migrationBuilder.DropColumn(
                name: "EventSchemaVersion",
                table: "problem_report_revisions");

            migrationBuilder.DropColumn(
                name: "EvidenceJson",
                table: "problem_report_revisions");
        }
    }
}
