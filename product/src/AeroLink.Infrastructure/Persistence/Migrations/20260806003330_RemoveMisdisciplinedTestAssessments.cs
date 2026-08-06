using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Removes test assessments whose discipline does not match the change request they were raised from.
    ///
    /// A verification discipline assesses its own level's change requests: System test assesses SRCRs, HLR
    /// test assesses HLRCRs, LLR test assesses LLRCRs. One row in the showcase database broke that — an HLR
    /// test assessment raised from a System change request, because that change request once modified an HLR
    /// requirement, back before `EnsureRequirementLevel` refused the combination. The row was superseded and
    /// inert, and still appeared in the HLR coverage queue naming a System change request.
    ///
    /// This deletes rather than retains. That is a deliberate exception to the product's habit of keeping
    /// superseded records as history, taken because the demonstration database is not a controlled record set
    /// and a wrong row in a queue teaches the wrong thing about how the queue works. It is written as a rule
    /// rather than as one identifier so that any equivalent row, in any database this runs against, goes with
    /// it.
    /// </summary>
    public partial class RemoveMisdisciplinedTestAssessments : Migration
    {
        private const string Misdisciplined = """
            SELECT r."Id"
              FROM test_change_reviews r
              JOIN system_change_requests s ON s."Id" = r."ChangeRequestId"
             WHERE (r."Discipline" = 'System'             AND s."Type" <> 'System')
                OR (r."Discipline" = 'HighLevelSoftware'  AND (s."Type" <> 'Software' OR s."SoftwareLevel" IS DISTINCT FROM 'HighLevel'))
                OR (r."Discipline" = 'LowLevelSoftware'   AND (s."Type" <> 'Software' OR s."SoftwareLevel" IS DISTINCT FROM 'LowLevel'))
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Children first. Anything added later hangs off the review by a cascading foreign key and goes
            // with the parent, so only the rows that predate cascade are named here.
            migrationBuilder.Sql($"""
                DELETE FROM verification_impact_items
                 WHERE "TestChangeReviewId" IN ({Misdisciplined});
                """);
            migrationBuilder.Sql($"""
                DELETE FROM test_change_request_claims
                 WHERE "TestChangeReviewId" IN ({Misdisciplined});
                """);
            migrationBuilder.Sql($"""
                DELETE FROM test_change_reviews
                 WHERE "Id" IN ({Misdisciplined});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. The rows are gone; a rollback cannot invent them, and pretending otherwise
            // would be worse than saying so.
        }
    }
}
