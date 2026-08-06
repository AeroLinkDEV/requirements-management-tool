using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Finishes the SCR/SWCR rename in the three places that cache a change request's number.
    ///
    /// The rename in `RenameChangeRequestIdentifiers` renamed the source of truth and one denormalised copy —
    /// the frozen baseline selections — and missed three more. Every downstream assessment, every link from
    /// an assessment to the change request answering it, and every test change review still displayed the
    /// retired name, because each keeps its own copy of the number so a queue can be rendered without a join.
    /// That is what a reader has been seeing on the change-request and coverage pages ever since.
    ///
    /// Each copy is rewritten from its own change request through the foreign key it already holds, rather
    /// than by mapping prefixes. By this point the source of truth is correct, so there is nothing to infer:
    /// the answer is simply what the change request is called, with the revision suffix the copy carried.
    /// </summary>
    public partial class RenameDenormalisedChangeRequestNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $finish$
                DECLARE
                    remaining INT;
                BEGIN
                    -- The change a consuming discipline was asked to assess.
                    UPDATE downstream_change_assessments a
                       SET "SourceChangeRequestNumber" =
                           s."BaseNumber" || COALESCE(substring(a."SourceChangeRequestNumber" from '\.[0-9]+$'), '')
                      FROM system_change_requests s
                     WHERE s."Id" = a."SourceChangeRequestId"
                       AND a."SourceChangeRequestNumber" NOT LIKE s."BaseNumber" || '%';

                    -- The change request an assessment linked as carrying its downstream change.
                    UPDATE downstream_assessment_change_request_links l
                       SET "ChangeRequestNumber" =
                           s."BaseNumber" || COALESCE(substring(l."ChangeRequestNumber" from '\.[0-9]+$'), '')
                      FROM system_change_requests s
                     WHERE s."Id" = l."ChangeRequestId"
                       AND l."ChangeRequestNumber" NOT LIKE s."BaseNumber" || '%';

                    -- The change a test assessment was raised from.
                    UPDATE test_change_reviews r
                       SET "SourceChangeRequestNumber" =
                           s."BaseNumber" || COALESCE(substring(r."SourceChangeRequestNumber" from '\.[0-9]+$'), '')
                      FROM system_change_requests s
                     WHERE s."Id" = r."ChangeRequestId"
                       AND r."SourceChangeRequestNumber" NOT LIKE s."BaseNumber" || '%';

                    -- A retired prefix surviving anywhere here means a copy whose change request could not be
                    -- resolved, which would leave the product displaying a name that no longer exists.
                    SELECT count(*) INTO remaining FROM (
                        SELECT "SourceChangeRequestNumber" AS n FROM downstream_change_assessments
                        UNION ALL SELECT "ChangeRequestNumber" FROM downstream_assessment_change_request_links
                        UNION ALL SELECT "SourceChangeRequestNumber" FROM test_change_reviews
                    ) copies
                    WHERE n LIKE 'SCR-%' OR n LIKE 'SWCR-%';

                    IF remaining > 0 THEN
                        RAISE EXCEPTION
                            'Rename incomplete: % cached change-request number(s) still carry a retired prefix.',
                            remaining;
                    END IF;
                END
                $finish$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Reversing this would rewrite correct identifiers back to retired ones,
            // which is a data loss dressed as a rollback: the retired names are not recoverable from the
            // current ones for a software change request, because SWCR maps to HLRCR or LLRCR by level.
        }
    }
}
