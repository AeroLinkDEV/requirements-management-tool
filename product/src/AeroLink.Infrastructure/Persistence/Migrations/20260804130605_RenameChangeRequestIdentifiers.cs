using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Renames every change-request identifier: SCR becomes SRCR, and SWCR becomes HLRCR or LLRCR according
    /// to the software level the record itself declares.
    ///
    /// This is not a string replacement. One retired prefix maps to two new ones, and only the change
    /// request knows which — so every occurrence, including the ones buried in narrative text, is resolved
    /// through the record it names. A blind replace would have relabelled roughly half the software change
    /// requests as the wrong level, which is the sort of error that reads as correct forever after.
    ///
    /// Numeric parts are preserved exactly. No record is renumbered: an identifier is a controlled record's
    /// identity, and changing the prefix is already as much as this migration is willing to do to it.
    ///
    /// Accepted consequence, decided by the product owner under issue #327: the frozen review-cycle snapshot
    /// hashes, the electronic signatures bound to them, and the frozen baseline content hash were all
    /// computed over the old identifiers and are deliberately left untouched. They no longer recompute from
    /// the records they attest to. Recomputing them was rejected outright — it would make a signature attest
    /// to content its signer never approved.
    /// </summary>
    public partial class RenameChangeRequestIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $rename$
                DECLARE
                    mapping RECORD;
                BEGIN
                    -- One row per change request: what it is called now, and what it must be called. The new
                    -- prefix comes from the same rule the application uses, so a record created a minute
                    -- after this migration is named exactly as one renamed by it.
                    CREATE TEMP TABLE change_request_rename ON COMMIT DROP AS
                    SELECT
                        "BaseNumber" AS old_base,
                        CASE
                            WHEN "Type" = 'System' THEN 'SRCR'
                            WHEN "SoftwareLevel" = 'HighLevel' THEN 'HLRCR'
                            WHEN "SoftwareLevel" = 'LowLevel' THEN 'LLRCR'
                        END || '-' || split_part("BaseNumber", '-', 2) AS new_base
                    FROM system_change_requests
                    WHERE "BaseNumber" LIKE 'SCR-%' OR "BaseNumber" LIKE 'SWCR-%';

                    -- A software change request with no level cannot be named, so it cannot be migrated
                    -- either. Refusing here is the only honest answer: guessing a level would put a wrong
                    -- name on a controlled record permanently.
                    IF EXISTS (SELECT 1 FROM change_request_rename WHERE new_base IS NULL) THEN
                        RAISE EXCEPTION
                            'Cannot rename: % change request(s) declare no software level and cannot be numbered HLRCR or LLRCR.',
                            (SELECT count(*) FROM change_request_rename WHERE new_base IS NULL);
                    END IF;

                    -- The identifiers themselves.
                    UPDATE system_change_requests scr
                       SET "BaseNumber" = m.new_base
                      FROM change_request_rename m
                     WHERE scr."BaseNumber" = m.old_base;

                    -- The denormalised copy a frozen baseline keeps of each selection's display number.
                    UPDATE baseline_scr_selections s
                       SET "ScrDisplayNumber" = m.new_base || substring(s."ScrDisplayNumber" from '\.[0-9]+$')
                      FROM change_request_rename m
                     WHERE s."ScrDisplayNumber" LIKE m.old_base || '.%';

                    -- Narrative text, resolved one identifier at a time. Longest first, so a five-digit
                    -- number can never match the leading characters of a longer one.
                    FOR mapping IN
                        SELECT old_base, new_base FROM change_request_rename ORDER BY length(old_base) DESC, old_base
                    LOOP
                        UPDATE audit_events
                           SET "Detail" = replace("Detail", mapping.old_base, mapping.new_base),
                               "EvidenceJson" = replace("EvidenceJson", mapping.old_base, mapping.new_base)
                         WHERE "Detail" LIKE '%' || mapping.old_base || '%'
                            OR "EvidenceJson" LIKE '%' || mapping.old_base || '%';

                        UPDATE baseline_events
                           SET "Detail" = replace("Detail", mapping.old_base, mapping.new_base)
                         WHERE "Detail" LIKE '%' || mapping.old_base || '%';

                        UPDATE electronic_signatures
                           SET "ArtifactRevision" = replace("ArtifactRevision", mapping.old_base, mapping.new_base),
                               "Meaning" = replace("Meaning", mapping.old_base, mapping.new_base)
                         WHERE "ArtifactRevision" LIKE '%' || mapping.old_base || '%'
                            OR "Meaning" LIKE '%' || mapping.old_base || '%';

                        UPDATE user_notifications
                           SET "Title" = replace("Title", mapping.old_base, mapping.new_base),
                               "Detail" = replace("Detail", mapping.old_base, mapping.new_base)
                         WHERE "Title" LIKE '%' || mapping.old_base || '%'
                            OR "Detail" LIKE '%' || mapping.old_base || '%';

                        UPDATE security_audit_events
                           SET "Target" = replace("Target", mapping.old_base, mapping.new_base),
                               "Detail" = replace("Detail", mapping.old_base, mapping.new_base)
                         WHERE "Target" LIKE '%' || mapping.old_base || '%'
                            OR "Detail" LIKE '%' || mapping.old_base || '%';

                        UPDATE jira_issue_links
                           SET "ArtifactNumber" = replace("ArtifactNumber", mapping.old_base, mapping.new_base)
                         WHERE "ArtifactNumber" LIKE '%' || mapping.old_base || '%';

                        -- Authored narrative. Rewritten under the same decision as the rest: the old names
                        -- are treated as a naming mistake being corrected, not as words worth preserving.
                        UPDATE system_change_requests
                           SET "Title" = replace("Title", mapping.old_base, mapping.new_base),
                               "Problem" = replace("Problem", mapping.old_base, mapping.new_base),
                               "Analysis" = replace("Analysis", mapping.old_base, mapping.new_base),
                               "Solution" = replace("Solution", mapping.old_base, mapping.new_base),
                               "ProblemRich" = replace("ProblemRich", mapping.old_base, mapping.new_base),
                               "AnalysisRich" = replace("AnalysisRich", mapping.old_base, mapping.new_base),
                               "SolutionRich" = replace("SolutionRich", mapping.old_base, mapping.new_base)
                         WHERE "Title" LIKE '%' || mapping.old_base || '%'
                            OR "Problem" LIKE '%' || mapping.old_base || '%'
                            OR "Analysis" LIKE '%' || mapping.old_base || '%'
                            OR "Solution" LIKE '%' || mapping.old_base || '%'
                            OR "ProblemRich" LIKE '%' || mapping.old_base || '%'
                            OR "AnalysisRich" LIKE '%' || mapping.old_base || '%'
                            OR "SolutionRich" LIKE '%' || mapping.old_base || '%';
                    END LOOP;

                    -- The stored artifact type of a controlled edit session. One legacy row still said 'SCR'
                    -- while every later one says 'ChangeRequest'.
                    UPDATE artifact_edit_sessions SET "ArtifactType" = 'ChangeRequest' WHERE "ArtifactType" = 'SCR';
                    UPDATE artifact_draft_snapshots SET "ArtifactType" = 'ChangeRequest' WHERE "ArtifactType" = 'SCR';

                    -- Numbering. Each new prefix resumes above the highest number already used at its own
                    -- level, so no future record can collide with a renamed one. The retired sequences are
                    -- removed rather than left behind to be found later and wondered about.
                    INSERT INTO identifier_sequences ("Id", "Scope", "NextValue", "ConcurrencyStamp")
                    SELECT gen_random_uuid(), prefix, next_value, 0
                      FROM (
                            SELECT split_part("BaseNumber", '-', 1) AS prefix,
                                   max(cast(split_part("BaseNumber", '-', 2) AS bigint)) + 1 AS next_value
                              FROM system_change_requests
                             WHERE "BaseNumber" ~ '^(SRCR|HLRCR|LLRCR)-[0-9]+$'
                             GROUP BY 1
                           ) resumed
                     WHERE NOT EXISTS (SELECT 1 FROM identifier_sequences s WHERE s."Scope" = resumed.prefix);

                    DELETE FROM identifier_sequences WHERE "Scope" IN ('SCR', 'SWCR');

                    -- Nothing may survive. This migration either leaves the database with no trace of the
                    -- retired prefixes or it leaves the database untouched.
                    IF EXISTS (SELECT 1 FROM system_change_requests WHERE "BaseNumber" ~ '^(SCR|SWCR)-')
                       OR EXISTS (SELECT 1 FROM baseline_scr_selections WHERE "ScrDisplayNumber" ~ '^(SCR|SWCR)-')
                       OR EXISTS (SELECT 1 FROM identifier_sequences WHERE "Scope" IN ('SCR', 'SWCR'))
                    THEN
                        RAISE EXCEPTION 'Rename incomplete: a retired change-request identifier survived.';
                    END IF;
                END
                $rename$;
                """);
        }

        /// <summary>
        /// Deliberately empty. Restoring the retired prefixes would require knowing which SWCR each HLRCR and
        /// LLRCR came from, and this migration destroys exactly that information by design — the product
        /// owner's decision was that no record of the former identifiers be kept. Rolling back is a restore
        /// from backup, not a down migration.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder) { }
    }
}
