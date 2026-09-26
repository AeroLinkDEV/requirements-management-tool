using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowOwnCaseTestChangeRequestOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_test_change_reviews_origin_xor",
                table: "test_change_reviews");

            migrationBuilder.AddCheckConstraint(
                name: "CK_test_change_reviews_origin_xor",
                table: "test_change_reviews",
                sql: "(\"OriginReferenceId\" <> '00000000-0000-0000-0000-000000000000' AND ((\"OriginKind\" = 'ChangeRequest' AND \"OriginReferenceId\" = \"ChangeRequestId\" AND \"ChangeRequestId\" IS NOT NULL AND \"OriginatingProblemReportId\" IS NULL) OR (\"OriginKind\" = 'ProblemReport' AND \"OriginReferenceId\" = \"OriginatingProblemReportId\" AND \"OriginatingProblemReportId\" IS NOT NULL AND \"ChangeRequestId\" IS NULL) OR (\"OriginKind\" IN ('CaseChange','CaseAssessment','CaseReview') AND \"ChangeRequestId\" IS NULL AND \"OriginatingProblemReportId\" IS NULL AND \"Discipline\" IN ('HighLevelSoftware','LowLevelSoftware') AND \"ArtifactKind\" = 'Procedure' AND \"SourceCaseOriginNumber\" <> '') OR (\"OriginKind\" = 'OwnCase' AND \"ChangeRequestId\" IS NULL AND \"OriginatingProblemReportId\" IS NULL AND (\"Discipline\" = 'System' OR \"ArtifactKind\" = 'Case'))))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_test_change_reviews_origin_xor",
                table: "test_change_reviews");

            migrationBuilder.AddCheckConstraint(
                name: "CK_test_change_reviews_origin_xor",
                table: "test_change_reviews",
                sql: "(\"OriginReferenceId\" <> '00000000-0000-0000-0000-000000000000' AND ((\"OriginKind\" = 'ChangeRequest' AND \"OriginReferenceId\" = \"ChangeRequestId\" AND \"ChangeRequestId\" IS NOT NULL AND \"OriginatingProblemReportId\" IS NULL) OR (\"OriginKind\" = 'ProblemReport' AND \"OriginReferenceId\" = \"OriginatingProblemReportId\" AND \"OriginatingProblemReportId\" IS NOT NULL AND \"ChangeRequestId\" IS NULL) OR (\"OriginKind\" IN ('CaseChange','CaseAssessment','CaseReview') AND \"ChangeRequestId\" IS NULL AND \"OriginatingProblemReportId\" IS NULL AND \"Discipline\" IN ('HighLevelSoftware','LowLevelSoftware') AND \"ArtifactKind\" = 'Procedure' AND \"SourceCaseOriginNumber\" <> '')))");
        }
    }
}
