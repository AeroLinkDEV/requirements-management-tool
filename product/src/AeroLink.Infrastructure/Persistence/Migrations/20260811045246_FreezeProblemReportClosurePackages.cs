using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FreezeProblemReportClosurePackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClosurePackageHash",
                table: "problem_report_closure_candidates",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ClosurePackageJson",
                table: "problem_report_closure_candidates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PackageProvenance",
                table: "problem_report_closure_candidates",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            // A candidate approved between the #461 and #451 migrations was never frozen as a final package.
            // Label it truthfully instead of presenting its live evidence graph as though it were historical.
            migrationBuilder.Sql("""
                UPDATE problem_report_closure_candidates
                SET "State" = 'LegacyUnavailable', "PackageProvenance" = 'LegacyClosureNotFrozen'
                WHERE "State" = 'Approved' AND "ClosurePackageJson" = '';

                UPDATE problem_report_closure_candidates
                SET "PackageProvenance" = 'Candidate'
                WHERE "PackageProvenance" = '';

                INSERT INTO problem_report_closure_candidates
                    ("Id", "ProblemReportId", "ReportRevision", "Sequence", "SchemaVersion", "ReportVersion",
                     "ReportSnapshotJson", "ReportSnapshotHash", "VerificationExecutionId",
                     "VerificationEvidenceJson", "VerificationEvidenceHash", "LinksManifestJson", "LinksManifestHash",
                     "ManifestHash", "SelectedBy", "SelectedAt", "State", "InvalidatedBy", "InvalidatedAt",
                     "InvalidationReason", "ApprovedByAccountId", "ApprovedBy", "ApprovedAt",
                     "ClosurePackageHash", "ClosurePackageJson", "PackageProvenance")
                SELECT pr."Id", pr."Id", pr."Revision", 0, 1, pr."Version",
                       '{"contract":"aerolink.problem-report-legacy-closure","provenance":"LegacyClosureNotFrozen"}', '', pr."Id",
                       '{"provenance":"LegacyClosureNotFrozen","evidence":"unavailable"}', '',
                       '{"provenance":"LegacyClosureNotFrozen","relationships":"unavailable"}', '', '',
                       'legacy-migration', COALESCE(pr."ClosureApprovedAt", pr."UpdatedAt"), 'LegacyUnavailable', '', NULL, '',
                       pr."ClosureApprovedBy", pr."ClosureApprovedByName", pr."ClosureApprovedAt", '', '', 'LegacyClosureNotFrozen'
                FROM problem_reports pr
                WHERE pr."State" = 'Closed'
                  AND NOT EXISTS (
                      SELECT 1 FROM problem_report_closure_candidates candidate
                      WHERE candidate."ProblemReportId" = pr."Id"
                        AND candidate."ReportRevision" = pr."Revision"
                        AND candidate."State" IN ('Approved', 'LegacyUnavailable'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosurePackageHash",
                table: "problem_report_closure_candidates");

            migrationBuilder.DropColumn(
                name: "ClosurePackageJson",
                table: "problem_report_closure_candidates");

            migrationBuilder.DropColumn(
                name: "PackageProvenance",
                table: "problem_report_closure_candidates");
        }
    }
}
