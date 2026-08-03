using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemediateLiveQualificationData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.ActiveProvider.Contains("Npgsql")) return;

            // `SWBL-00000900.00 / Probe baseline` was created against the persistent FMSLIVE database during
            // an attended qualification. It is not programme configuration. The exact pre-cleanup database is
            // protected by the required manifested backup; this migration removes only that proven residue.
            //
            // One procedure was deliberately authored later against the revision materialized by the probe.
            // Preserve that controlled procedure by moving its coverage to the predecessor revision. Duplicate
            // suspect coverage already carried by the predecessor is removed first.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    probe_id uuid;
                    contaminated record;
                    predecessor_revision_id uuid;
                BEGIN
                    SELECT baseline."Id" INTO probe_id
                    FROM candidate_baselines baseline
                    JOIN projects project ON project."Id" = baseline."ProjectId"
                    JOIN programs program ON program."Id" = project."ProgramId"
                    WHERE program."Code" = 'FMSLIVE'
                      AND baseline."BaseNumber" = 'SWBL-00000900'
                      AND baseline."Revision" = 0
                      AND baseline."Name" = 'Probe baseline';

                    IF probe_id IS NULL THEN
                        RETURN;
                    END IF;

                    FOR contaminated IN
                        SELECT revision."Id", revision."ArtifactId", revision."Revision"
                        FROM requirement_revisions revision
                        WHERE revision."EffectiveBaselineId" = probe_id
                    LOOP
                        SELECT prior."Id" INTO predecessor_revision_id
                        FROM requirement_revisions prior
                        WHERE prior."ArtifactId" = contaminated."ArtifactId"
                          AND prior."Revision" < contaminated."Revision"
                        ORDER BY prior."Revision" DESC
                        LIMIT 1;

                        IF predecessor_revision_id IS NULL THEN
                            RAISE EXCEPTION 'Probe cleanup cannot preserve requirement revision % because no predecessor exists.', contaminated."Id";
                        END IF;

                        DELETE FROM test_requirement_coverage coverage
                        WHERE coverage."RequirementRevisionId" = contaminated."Id"
                          AND EXISTS (
                              SELECT 1 FROM test_requirement_coverage retained
                              WHERE retained."ProcedureRevisionId" = coverage."ProcedureRevisionId"
                                AND retained."RequirementRevisionId" = predecessor_revision_id);

                        UPDATE test_requirement_coverage
                        SET "RequirementRevisionId" = predecessor_revision_id,
                            "IsSuspect" = false,
                            "SuspectReason" = '',
                            "SuspectSince" = NULL,
                            "ConfirmedBy" = NULL,
                            "ConfirmedAt" = NULL
                        WHERE "RequirementRevisionId" = contaminated."Id";

                        DELETE FROM requirement_trace_links
                        WHERE "SourceRevisionId" = contaminated."Id" OR "TargetRevisionId" = contaminated."Id";
                        DELETE FROM requirement_revision_tags WHERE "RevisionId" = contaminated."Id";
                        DELETE FROM requirement_revision_profiles WHERE "RevisionId" = contaminated."Id";
                    END LOOP;

                    DELETE FROM baseline_requirement_selections WHERE "BaselineId" = probe_id;
                    DELETE FROM requirement_revisions WHERE "EffectiveBaselineId" = probe_id;
                    DELETE FROM candidate_baselines WHERE "Id" = probe_id;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally non-reversible. Restoring qualification residue would recreate a false controlled
            // baseline; the verified pre-migration backup is the recovery path if this remediation is rejected.
        }
    }
}
