using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VersionProblemReportEvidenceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SnapshotSchemaVersion",
                table: "problem_report_revisions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReportSnapshotSchemaVersion",
                table: "problem_report_closure_candidates",
                type: "integer",
                nullable: false,
                // Existing candidates used the original version-1 closure-review snapshot. Preserve that
                // provenance without changing their JSON or hashes; new candidates explicitly write v2.
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SnapshotSchemaVersion",
                table: "problem_report_revisions");

            migrationBuilder.DropColumn(
                name: "ReportSnapshotSchemaVersion",
                table: "problem_report_closure_candidates");
        }
    }
}
