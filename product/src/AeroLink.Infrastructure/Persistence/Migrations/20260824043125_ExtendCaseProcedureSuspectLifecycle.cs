using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtendCaseProcedureSuspectLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_exact_link_suspect_events_requirement_revisions_CauseRequir~",
                table: "exact_link_suspect_events");

            migrationBuilder.DropForeignKey(
                name: "FK_exact_link_suspect_lifecycles_requirement_revisions_CauseRe~",
                table: "exact_link_suspect_lifecycles");

            migrationBuilder.DropIndex(
                // #725 named this before PostgreSQL's 63-byte limit was applied, so PostgreSQL persisted
                // the truncated trailing-underscore name. The current EF convention's `~` name does not
                // identify that predecessor object and would make a clean exact-predecessor upgrade fail.
                name: "IX_test_change_reviews_OriginKind_OriginReferenceId_Discipline_",
                table: "test_change_reviews");

            migrationBuilder.DropCheckConstraint(
                name: "CK_test_change_reviews_origin_xor",
                table: "test_change_reviews");

            migrationBuilder.DropIndex(
                name: "IX_exact_link_suspect_lifecycles_CauseRequirementRevisionId",
                table: "exact_link_suspect_lifecycles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exact_link_suspect_lifecycle_cause_xor",
                table: "exact_link_suspect_lifecycles");

            migrationBuilder.DropIndex(
                name: "IX_exact_link_suspect_events_CauseRequirementRevisionId",
                table: "exact_link_suspect_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exact_link_suspect_event_cause_xor",
                table: "exact_link_suspect_events");

            migrationBuilder.AddColumn<Guid>(
                name: "ExactLinkSuspectLifecycleId",
                table: "test_case_procedure_links",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CauseVerificationRevisionId",
                table: "exact_link_suspect_lifecycles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CauseVerificationRevisionId",
                table: "exact_link_suspect_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_OriginKind_OriginReferenceId_Discipline~",
                table: "test_change_reviews",
                columns: new[] { "OriginKind", "OriginReferenceId", "Discipline", "ArtifactKind", "Revision" },
                unique: true,
                filter: "\"OriginKind\" IN ('CaseChange','CaseAssessment','CaseReview')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_test_change_reviews_origin_xor",
                table: "test_change_reviews",
                sql: "(\"OriginReferenceId\" <> '00000000-0000-0000-0000-000000000000' AND ((\"OriginKind\" = 'ChangeRequest' AND \"OriginReferenceId\" = \"ChangeRequestId\" AND \"ChangeRequestId\" IS NOT NULL AND \"OriginatingProblemReportId\" IS NULL) OR (\"OriginKind\" = 'ProblemReport' AND \"OriginReferenceId\" = \"OriginatingProblemReportId\" AND \"OriginatingProblemReportId\" IS NOT NULL AND \"ChangeRequestId\" IS NULL) OR (\"OriginKind\" IN ('CaseChange','CaseAssessment','CaseReview') AND \"ChangeRequestId\" IS NULL AND \"OriginatingProblemReportId\" IS NULL AND \"Discipline\" IN ('HighLevelSoftware','LowLevelSoftware') AND \"ArtifactKind\" = 'Procedure' AND \"SourceCaseOriginNumber\" <> '')))");

            migrationBuilder.CreateIndex(
                name: "IX_test_case_procedure_links_ExactLinkSuspectLifecycleId",
                table: "test_case_procedure_links",
                column: "ExactLinkSuspectLifecycleId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_exact_link_suspect_lifecycle_cause_xor",
                table: "exact_link_suspect_lifecycles",
                sql: "((\"LinkKind\" = 'RequirementTrace' AND \"CauseKind\" = 'InternalRequirementRevision' AND \"CauseRequirementRevisionId\" IS NOT NULL AND \"CauseBaselineImportId\" IS NULL AND \"CauseVerificationRevisionId\" IS NULL) OR (\"LinkKind\" = 'RequirementTrace' AND \"CauseKind\" = 'ExternalBaselineImport' AND \"CauseRequirementRevisionId\" IS NULL AND \"CauseBaselineImportId\" IS NOT NULL AND \"CauseVerificationRevisionId\" IS NULL) OR (\"LinkKind\" = 'CaseProcedure' AND \"CauseKind\" = 'InternalVerificationRevision' AND \"CauseRequirementRevisionId\" IS NULL AND \"CauseBaselineImportId\" IS NULL AND \"CauseVerificationRevisionId\" IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_exact_link_suspect_event_cause_xor",
                table: "exact_link_suspect_events",
                sql: "((\"LinkKind\" = 'RequirementTrace' AND \"CauseKind\" = 'InternalRequirementRevision' AND \"CauseRequirementRevisionId\" IS NOT NULL AND \"CauseBaselineImportId\" IS NULL AND \"CauseVerificationRevisionId\" IS NULL) OR (\"LinkKind\" = 'RequirementTrace' AND \"CauseKind\" = 'ExternalBaselineImport' AND \"CauseRequirementRevisionId\" IS NULL AND \"CauseBaselineImportId\" IS NOT NULL AND \"CauseVerificationRevisionId\" IS NULL) OR (\"LinkKind\" = 'CaseProcedure' AND \"CauseKind\" = 'InternalVerificationRevision' AND \"CauseRequirementRevisionId\" IS NULL AND \"CauseBaselineImportId\" IS NULL AND \"CauseVerificationRevisionId\" IS NOT NULL))");

            migrationBuilder.AddForeignKey(
                name: "FK_test_case_procedure_links_exact_link_suspect_lifecycles_Exa~",
                table: "test_case_procedure_links",
                column: "ExactLinkSuspectLifecycleId",
                principalTable: "exact_link_suspect_lifecycles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "fn_validate_test_change_review_case_origin"()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."OriginKind" = 'CaseChange' THEN
                        IF NOT EXISTS (
                            SELECT 1 FROM "test_procedure_changes" c
                            JOIN "test_change_reviews" parent ON parent."Id" = c."TestChangeReviewId"
                            WHERE c."Id" = NEW."OriginReferenceId"
                              AND parent."ProjectId" = NEW."ProjectId" AND parent."ReleaseId" = NEW."ReleaseId"
                              AND parent."Discipline" = NEW."Discipline" AND parent."ArtifactKind" = 'Case'
                              AND (parent."State" = 'Approved' OR (parent."State" = 'Superseded'
                                  AND (NEW."Revision" > 0 OR TG_OP <> 'INSERT')))
                              AND c."BaseNumber" <> ''
                              AND NEW."SourceCaseOriginNumber" = c."BaseNumber" || '.' || LPAD(c."Revision"::text, 2, '0')
                        ) THEN RAISE EXCEPTION 'CaseChange origin is not an exact approved software Case change'; END IF;
                    ELSIF NEW."OriginKind" = 'CaseAssessment' THEN
                        IF NOT EXISTS (
                            SELECT 1 FROM "verification_impact_items" item
                            JOIN "test_change_reviews" parent ON parent."Id" = item."TestChangeReviewId"
                            WHERE item."Id" = NEW."OriginReferenceId"
                              AND item."ProjectId" = NEW."ProjectId" AND item."ReleaseId" = NEW."ReleaseId"
                              AND parent."Discipline" = NEW."Discipline" AND parent."ArtifactKind" = 'Case'
                              AND (parent."State" <> 'Superseded' OR NEW."Revision" > 0 OR TG_OP <> 'INSERT')
                              AND item."State" = 'Resolved' AND item."Outcome" = 'NewProcedureRequired'
                              AND item."ProcedureChangeAction" = 'CreateNew'
                              AND item."RequirementRevisionId" IS NOT NULL
                              AND NEW."SourceCaseOriginNumber" = item."SubjectDisplayNumber"
                        ) THEN RAISE EXCEPTION 'CaseAssessment origin is not an exact resolved Case assessment'; END IF;
                    ELSIF NEW."OriginKind" = 'CaseReview' THEN
                        IF NOT EXISTS (
                            SELECT 1 FROM "test_change_reviews" parent
                            WHERE parent."Id" = NEW."OriginReferenceId"
                              AND parent."ProjectId" = NEW."ProjectId" AND parent."ReleaseId" = NEW."ReleaseId"
                              AND parent."Discipline" = NEW."Discipline" AND parent."ArtifactKind" = 'Case'
                              AND (parent."State" = 'Approved' OR (parent."State" = 'Superseded'
                                  AND (NEW."Revision" > 0 OR TG_OP <> 'INSERT')))
                              AND NEW."SourceCaseOriginNumber" = parent."BaseNumber" || '.' || LPAD(parent."Revision"::text, 2, '0')
                        ) THEN RAISE EXCEPTION 'CaseReview origin is not an exact approved software Case package'; END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE OR REPLACE FUNCTION aerolink_refuse_case_review_origin_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF EXISTS (SELECT 1 FROM test_change_reviews dependent
                        WHERE dependent."OriginKind" = 'CaseReview'
                          AND dependent."OriginReferenceId" = OLD."Id") THEN
                        IF TG_OP = 'DELETE' THEN
                            RAISE EXCEPTION 'A Case package used as a Procedure assessment origin cannot be deleted.';
                        END IF;
                        IF NEW."ProjectId" IS DISTINCT FROM OLD."ProjectId"
                           OR NEW."ReleaseId" IS DISTINCT FROM OLD."ReleaseId"
                           OR NEW."Discipline" IS DISTINCT FROM OLD."Discipline"
                           OR NEW."ArtifactKind" IS DISTINCT FROM OLD."ArtifactKind"
                           OR NEW."BaseNumber" IS DISTINCT FROM OLD."BaseNumber"
                           OR NEW."Revision" IS DISTINCT FROM OLD."Revision"
                           OR NEW."ChangeRequestId" IS DISTINCT FROM OLD."ChangeRequestId"
                           OR (NEW."State" IS DISTINCT FROM OLD."State"
                               AND NOT (OLD."State" = 'Approved' AND NEW."State" = 'Superseded')) THEN
                            RAISE EXCEPTION 'A Case package used as a Procedure assessment origin is immutable.';
                        END IF;
                    END IF;
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER trg_refuse_case_review_origin_update
                BEFORE UPDATE ON test_change_reviews FOR EACH ROW
                EXECUTE FUNCTION aerolink_refuse_case_review_origin_mutation();
                CREATE TRIGGER trg_refuse_case_review_origin_delete
                BEFORE DELETE ON test_change_reviews FOR EACH ROW
                EXECUTE FUNCTION aerolink_refuse_case_review_origin_mutation();

                CREATE OR REPLACE FUNCTION aerolink_validate_exact_link_lifecycle_cause()
                RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NEW."LinkKind" = 'RequirementTrace'
                       AND NEW."CauseKind" = 'InternalRequirementRevision'
                       AND NOT EXISTS (SELECT 1 FROM requirement_revisions
                           WHERE "Id" = NEW."CauseRequirementRevisionId") THEN
                        RAISE EXCEPTION 'An internal exact-link cause must name an existing requirement revision.';
                    ELSIF NEW."LinkKind" = 'CaseProcedure'
                       AND NEW."CauseKind" = 'InternalVerificationRevision'
                       AND NOT EXISTS (
                           SELECT 1 FROM test_procedure_revisions revision
                           JOIN test_procedures artifact ON artifact."Id" = revision."ProcedureId"
                           WHERE revision."Id" = NEW."CauseVerificationRevisionId"
                             AND artifact."ArtifactKind" = 'Case'
                             AND artifact."ProjectId" = NEW."ProjectId") THEN
                        RAISE EXCEPTION 'A Case-to-Procedure cause must name an existing Case revision in its project.';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER trg_validate_exact_link_lifecycle_cause
                BEFORE INSERT ON exact_link_suspect_lifecycles FOR EACH ROW
                EXECUTE FUNCTION aerolink_validate_exact_link_lifecycle_cause();

                CREATE OR REPLACE FUNCTION aerolink_refuse_exact_link_lifecycle_identity_update()
                RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NEW."ProjectId" IS DISTINCT FROM OLD."ProjectId"
                       OR NEW."LinkKind" IS DISTINCT FROM OLD."LinkKind"
                       OR NEW."LinkId" IS DISTINCT FROM OLD."LinkId"
                       OR NEW."CauseKind" IS DISTINCT FROM OLD."CauseKind"
                       OR NEW."CauseRequirementRevisionId" IS DISTINCT FROM OLD."CauseRequirementRevisionId"
                       OR NEW."CauseBaselineImportId" IS DISTINCT FROM OLD."CauseBaselineImportId"
                       OR NEW."CauseVerificationRevisionId" IS DISTINCT FROM OLD."CauseVerificationRevisionId"
                       OR NEW."RaisedBy" IS DISTINCT FROM OLD."RaisedBy"
                       OR NEW."RaisedAt" IS DISTINCT FROM OLD."RaisedAt"
                       OR NEW."RaisedRationale" IS DISTINCT FROM OLD."RaisedRationale" THEN
                        RAISE EXCEPTION 'An exact-link lifecycle identity, cause, and raised attribution are immutable.';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER trg_exact_link_lifecycle_identity_immutable
                BEFORE UPDATE ON exact_link_suspect_lifecycles FOR EACH ROW
                EXECUTE FUNCTION aerolink_refuse_exact_link_lifecycle_identity_update();

                CREATE OR REPLACE FUNCTION aerolink_enforce_case_procedure_lifecycle_exact()
                RETURNS trigger LANGUAGE plpgsql AS $body$
                DECLARE lifecycle record;
                BEGIN
                    IF TG_OP = 'UPDATE'
                       AND (NEW."CaseRevisionId" IS DISTINCT FROM OLD."CaseRevisionId"
                           OR NEW."ProcedureRevisionId" IS DISTINCT FROM OLD."ProcedureRevisionId") THEN
                        RAISE EXCEPTION 'An exact Case-to-Procedure relation cannot be retargeted; create a successor relation.';
                    END IF;
                    IF TG_OP = 'UPDATE'
                       AND NEW."ExactLinkSuspectLifecycleId" IS DISTINCT FROM OLD."ExactLinkSuspectLifecycleId" THEN
                        RAISE EXCEPTION 'A Case-to-Procedure relation cannot change its immutable suspect lifecycle association.';
                    END IF;
                    IF NEW."ExactLinkSuspectLifecycleId" IS NULL THEN RETURN NEW; END IF;
                    SELECT * INTO lifecycle FROM exact_link_suspect_lifecycles
                    WHERE "Id" = NEW."ExactLinkSuspectLifecycleId";
                    IF lifecycle."LinkKind" <> 'CaseProcedure' OR lifecycle."LinkId" <> NEW."Id"
                       OR lifecycle."CauseKind" <> 'InternalVerificationRevision'
                       OR lifecycle."CauseVerificationRevisionId" <> NEW."CaseRevisionId" THEN
                        RAISE EXCEPTION 'A Case-to-Procedure lifecycle must identify its exact carried link and changed Case revision.';
                    END IF;
                    IF NOT EXISTS (
                        SELECT 1 FROM test_procedure_revisions case_revision
                        JOIN test_procedures case_artifact ON case_artifact."Id" = case_revision."ProcedureId"
                        JOIN test_procedure_revisions procedure_revision ON procedure_revision."Id" = NEW."ProcedureRevisionId"
                        JOIN test_procedures procedure_artifact ON procedure_artifact."Id" = procedure_revision."ProcedureId"
                        WHERE case_revision."Id" = NEW."CaseRevisionId"
                          AND case_artifact."ProjectId" = lifecycle."ProjectId"
                          AND procedure_artifact."ProjectId" = lifecycle."ProjectId"
                          AND case_artifact."ArtifactKind" = 'Case'
                          AND procedure_artifact."ArtifactKind" = 'Procedure'
                          AND case_artifact."Level" = procedure_artifact."Level") THEN
                        RAISE EXCEPTION 'A Case-to-Procedure lifecycle must remain within one project and discipline.';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER trg_case_procedure_lifecycle_exact
                BEFORE INSERT OR UPDATE ON test_case_procedure_links FOR EACH ROW
                EXECUTE FUNCTION aerolink_enforce_case_procedure_lifecycle_exact();

                CREATE OR REPLACE FUNCTION aerolink_enforce_exact_link_event_attribution()
                RETURNS trigger LANGUAGE plpgsql AS $body$
                DECLARE lifecycle record;
                BEGIN
                    SELECT * INTO lifecycle FROM exact_link_suspect_lifecycles WHERE "Id" = NEW."LifecycleId";
                    IF lifecycle."ProjectId" <> NEW."ProjectId" OR lifecycle."LinkKind" <> NEW."LinkKind"
                       OR lifecycle."LinkId" <> NEW."LinkId" OR lifecycle."CauseKind" <> NEW."CauseKind"
                       OR lifecycle."CauseRequirementRevisionId" IS DISTINCT FROM NEW."CauseRequirementRevisionId"
                       OR lifecycle."CauseBaselineImportId" IS DISTINCT FROM NEW."CauseBaselineImportId"
                       OR lifecycle."CauseVerificationRevisionId" IS DISTINCT FROM NEW."CauseVerificationRevisionId" THEN
                        RAISE EXCEPTION 'An exact-link event must retain its lifecycle exact attribution.';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER trg_exact_link_event_attribution BEFORE INSERT ON exact_link_suspect_events
                FOR EACH ROW EXECUTE FUNCTION aerolink_enforce_exact_link_event_attribution();

                CREATE OR REPLACE FUNCTION aerolink_refuse_exact_link_evidence_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN RAISE EXCEPTION 'Exact-link lifecycle evidence cannot be changed or deleted.'; END;
                $body$;
                CREATE TRIGGER trg_exact_link_event_immutable BEFORE UPDATE OR DELETE ON exact_link_suspect_events
                FOR EACH ROW EXECUTE FUNCTION aerolink_refuse_exact_link_evidence_mutation();
                CREATE TRIGGER trg_exact_link_lifecycle_immutable_delete BEFORE DELETE ON exact_link_suspect_lifecycles
                FOR EACH ROW EXECUTE FUNCTION aerolink_refuse_exact_link_evidence_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_exact_link_lifecycle_immutable_delete ON exact_link_suspect_lifecycles;
                DROP TRIGGER IF EXISTS trg_exact_link_event_immutable ON exact_link_suspect_events;
                DROP FUNCTION IF EXISTS aerolink_refuse_exact_link_evidence_mutation();
                DROP TRIGGER IF EXISTS trg_exact_link_event_attribution ON exact_link_suspect_events;
                DROP FUNCTION IF EXISTS aerolink_enforce_exact_link_event_attribution();
                DROP TRIGGER IF EXISTS trg_case_procedure_lifecycle_exact ON test_case_procedure_links;
                DROP FUNCTION IF EXISTS aerolink_enforce_case_procedure_lifecycle_exact();
                DROP TRIGGER IF EXISTS trg_exact_link_lifecycle_identity_immutable ON exact_link_suspect_lifecycles;
                DROP FUNCTION IF EXISTS aerolink_refuse_exact_link_lifecycle_identity_update();
                DROP TRIGGER IF EXISTS trg_validate_exact_link_lifecycle_cause ON exact_link_suspect_lifecycles;
                DROP FUNCTION IF EXISTS aerolink_validate_exact_link_lifecycle_cause();
                DROP TRIGGER IF EXISTS trg_refuse_case_review_origin_update ON test_change_reviews;
                DROP TRIGGER IF EXISTS trg_refuse_case_review_origin_delete ON test_change_reviews;
                DROP FUNCTION IF EXISTS aerolink_refuse_case_review_origin_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_test_case_procedure_links_exact_link_suspect_lifecycles_Exa~",
                table: "test_case_procedure_links");

            migrationBuilder.DropIndex(
                name: "IX_test_change_reviews_OriginKind_OriginReferenceId_Discipline~",
                table: "test_change_reviews");

            migrationBuilder.DropCheckConstraint(
                name: "CK_test_change_reviews_origin_xor",
                table: "test_change_reviews");

            migrationBuilder.DropIndex(
                name: "IX_test_case_procedure_links_ExactLinkSuspectLifecycleId",
                table: "test_case_procedure_links");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exact_link_suspect_lifecycle_cause_xor",
                table: "exact_link_suspect_lifecycles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exact_link_suspect_event_cause_xor",
                table: "exact_link_suspect_events");

            migrationBuilder.DropColumn(
                name: "ExactLinkSuspectLifecycleId",
                table: "test_case_procedure_links");

            migrationBuilder.DropColumn(
                name: "CauseVerificationRevisionId",
                table: "exact_link_suspect_lifecycles");

            migrationBuilder.DropColumn(
                name: "CauseVerificationRevisionId",
                table: "exact_link_suspect_events");

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_OriginKind_OriginReferenceId_Discipline_",
                table: "test_change_reviews",
                columns: new[] { "OriginKind", "OriginReferenceId", "Discipline", "ArtifactKind", "Revision" },
                unique: true,
                filter: "\"OriginKind\" IN ('CaseChange','CaseAssessment')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_test_change_reviews_origin_xor",
                table: "test_change_reviews",
                sql: "(\"OriginReferenceId\" <> '00000000-0000-0000-0000-000000000000' AND ((\"OriginKind\" = 'ChangeRequest' AND \"OriginReferenceId\" = \"ChangeRequestId\" AND \"ChangeRequestId\" IS NOT NULL AND \"OriginatingProblemReportId\" IS NULL) OR (\"OriginKind\" = 'ProblemReport' AND \"OriginReferenceId\" = \"OriginatingProblemReportId\" AND \"OriginatingProblemReportId\" IS NOT NULL AND \"ChangeRequestId\" IS NULL) OR (\"OriginKind\" IN ('CaseChange','CaseAssessment') AND \"ChangeRequestId\" IS NULL AND \"OriginatingProblemReportId\" IS NULL AND \"Discipline\" IN ('HighLevelSoftware','LowLevelSoftware') AND \"ArtifactKind\" = 'Procedure' AND \"SourceCaseOriginNumber\" <> '')))");

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_lifecycles_CauseRequirementRevisionId",
                table: "exact_link_suspect_lifecycles",
                column: "CauseRequirementRevisionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_exact_link_suspect_lifecycle_cause_xor",
                table: "exact_link_suspect_lifecycles",
                sql: "((\"CauseKind\" = 'InternalRequirementRevision' AND \"CauseRequirementRevisionId\" IS NOT NULL AND \"CauseBaselineImportId\" IS NULL) OR (\"CauseKind\" = 'ExternalBaselineImport' AND \"CauseRequirementRevisionId\" IS NULL AND \"CauseBaselineImportId\" IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_events_CauseRequirementRevisionId",
                table: "exact_link_suspect_events",
                column: "CauseRequirementRevisionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_exact_link_suspect_event_cause_xor",
                table: "exact_link_suspect_events",
                sql: "((\"CauseKind\" = 'InternalRequirementRevision' AND \"CauseRequirementRevisionId\" IS NOT NULL AND \"CauseBaselineImportId\" IS NULL) OR (\"CauseKind\" = 'ExternalBaselineImport' AND \"CauseRequirementRevisionId\" IS NULL AND \"CauseBaselineImportId\" IS NOT NULL))");

            migrationBuilder.AddForeignKey(
                name: "FK_exact_link_suspect_events_requirement_revisions_CauseRequir~",
                table: "exact_link_suspect_events",
                column: "CauseRequirementRevisionId",
                principalTable: "requirement_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_exact_link_suspect_lifecycles_requirement_revisions_CauseRe~",
                table: "exact_link_suspect_lifecycles",
                column: "CauseRequirementRevisionId",
                principalTable: "requirement_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
