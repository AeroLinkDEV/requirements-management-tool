using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProcedureTestChangeControlPackage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_test_change_reviews_ChangeRequestId_Discipline_Revision",
                table: "test_change_reviews");

            migrationBuilder.AddColumn<string>(
                name: "ArtifactKind",
                table: "test_change_reviews",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OriginKind",
                table: "test_change_reviews",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "OriginReferenceId",
                table: "test_change_reviews",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "SourceCaseOriginNumber",
                table: "test_change_reviews",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            // Historical packages had an implicit identity: System rows were Procedures and software rows
            // were Cases. Their two legacy origin columns are retained as projections, while the new
            // discriminator/reference pair becomes the authoritative immutable origin. Refuse rather than
            // fabricate if an old row has neither legacy origin; such a database is not safe to upgrade.
            migrationBuilder.Sql("""
                UPDATE "test_change_reviews"
                SET "ArtifactKind" = CASE WHEN "Discipline" = 'System' THEN 'Procedure' ELSE 'Case' END,
                    "OriginKind" = CASE WHEN "ChangeRequestId" IS NOT NULL THEN 'ChangeRequest' ELSE 'ProblemReport' END,
                    "OriginReferenceId" = COALESCE("ChangeRequestId", "OriginatingProblemReportId")
                WHERE "ArtifactKind" = '';
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "test_change_reviews"
                        WHERE ("ChangeRequestId" IS NULL AND "OriginatingProblemReportId" IS NULL)
                           OR "OriginReferenceId" IS NULL
                           OR "OriginReferenceId" = '00000000-0000-0000-0000-000000000000') THEN
                        RAISE EXCEPTION 'Cannot backfill a test change review without exactly one legacy origin';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_ChangeRequestId_Discipline_ArtifactKind~",
                table: "test_change_reviews",
                columns: new[] { "ChangeRequestId", "Discipline", "ArtifactKind", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_OriginKind_OriginReferenceId_Discipline_ArtifactKind_Revision",
                table: "test_change_reviews",
                columns: new[] { "OriginKind", "OriginReferenceId", "Discipline", "ArtifactKind", "Revision" },
                unique: true,
                filter: "\"OriginKind\" IN ('CaseChange','CaseAssessment')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_test_change_reviews_origin_xor",
                table: "test_change_reviews",
                sql: "(\"OriginReferenceId\" <> '00000000-0000-0000-0000-000000000000' AND ((\"OriginKind\" = 'ChangeRequest' AND \"OriginReferenceId\" = \"ChangeRequestId\" AND \"ChangeRequestId\" IS NOT NULL AND \"OriginatingProblemReportId\" IS NULL) OR (\"OriginKind\" = 'ProblemReport' AND \"OriginReferenceId\" = \"OriginatingProblemReportId\" AND \"OriginatingProblemReportId\" IS NOT NULL AND \"ChangeRequestId\" IS NULL) OR (\"OriginKind\" IN ('CaseChange','CaseAssessment') AND \"ChangeRequestId\" IS NULL AND \"OriginatingProblemReportId\" IS NULL AND \"Discipline\" IN ('HighLevelSoftware','LowLevelSoftware') AND \"ArtifactKind\" = 'Procedure' AND \"SourceCaseOriginNumber\" <> '')))");
            // OriginReferenceId is intentionally polymorphic: one immutable discriminated origin rather than
            // parallel nullable foreign-key columns. PostgreSQL therefore needs a discriminator-aware trigger
            // to reject wrong-table and dangling IDs on direct SQL writes.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "fn_validate_test_change_review_case_origin"()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."OriginKind" = 'CaseChange' THEN
                        IF NOT EXISTS (
                            SELECT 1 FROM "test_procedure_changes" c
                            JOIN "test_change_reviews" parent ON parent."Id" = c."TestChangeReviewId"
                            WHERE c."Id" = NEW."OriginReferenceId"
                              AND parent."ProjectId" = NEW."ProjectId"
                              AND parent."ReleaseId" = NEW."ReleaseId"
                              AND parent."Discipline" = NEW."Discipline"
                              AND parent."ArtifactKind" = 'Case'
                               AND (parent."State" = 'Approved'
                                    OR (parent."State" = 'Superseded'
                                        AND (NEW."Revision" > 0 OR TG_OP <> 'INSERT')))
                              AND c."BaseNumber" <> ''
                              AND NEW."SourceCaseOriginNumber" = c."BaseNumber" || '.' || LPAD(c."Revision"::text, 2, '0')
                        ) THEN
                            RAISE EXCEPTION 'CaseChange origin is not an exact approved software Case change in the same project/build/discipline';
                        END IF;
                    ELSIF NEW."OriginKind" = 'CaseAssessment' THEN
                        IF NOT EXISTS (
                            SELECT 1 FROM "verification_impact_items" item
                            JOIN "test_change_reviews" parent ON parent."Id" = item."TestChangeReviewId"
                            WHERE item."Id" = NEW."OriginReferenceId"
                              AND item."ProjectId" = NEW."ProjectId"
                              AND item."ReleaseId" = NEW."ReleaseId"
                              AND parent."Discipline" = NEW."Discipline"
                              AND parent."ArtifactKind" = 'Case'
                               AND (parent."State" <> 'Superseded'
                                    OR NEW."Revision" > 0
                                    OR TG_OP <> 'INSERT')
                              AND item."State" = 'Resolved'
                              AND item."Outcome" = 'NewProcedureRequired'
                              AND item."ProcedureChangeAction" = 'CreateNew'
                              AND item."RequirementRevisionId" IS NOT NULL
                              AND NEW."SourceCaseOriginNumber" = item."SubjectDisplayNumber"
                        ) THEN
                            RAISE EXCEPTION 'CaseAssessment origin is not an exact resolved NewProcedureRequired Case assessment';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE CONSTRAINT TRIGGER "trg_validate_test_change_review_case_origin"
                    AFTER INSERT OR UPDATE
                    ON "test_change_reviews"
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION "fn_validate_test_change_review_case_origin"();

                CREATE OR REPLACE FUNCTION "fn_refuse_test_change_review_case_origin_delete"()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "test_change_reviews"
                        WHERE (TG_TABLE_NAME = 'test_procedure_changes' AND "OriginKind" = 'CaseChange' AND "OriginReferenceId" = OLD."Id")
                           OR (TG_TABLE_NAME = 'verification_impact_items' AND "OriginKind" = 'CaseAssessment' AND "OriginReferenceId" = OLD."Id")
                    ) THEN
                        RAISE EXCEPTION 'Cannot delete a row that is the immutable origin of a test change review';
                    END IF;
                    RETURN OLD;
                END;
                $$;
                CREATE TRIGGER "trg_refuse_test_procedure_change_origin_delete"
                    BEFORE DELETE ON "test_procedure_changes"
                    FOR EACH ROW EXECUTE FUNCTION "fn_refuse_test_change_review_case_origin_delete"();
                CREATE TRIGGER "trg_refuse_verification_impact_origin_delete"
                    BEFORE DELETE ON "verification_impact_items"
                    FOR EACH ROW EXECUTE FUNCTION "fn_refuse_test_change_review_case_origin_delete"();

                -- A source package may advance from Approved to Superseded after a Procedure package was
                -- issued, but it cannot be reopened to a non-eligible state. The dependent revision itself
                -- remains valid because its origin retains the exact source child identity.
                CREATE OR REPLACE FUNCTION "fn_refuse_test_change_review_case_source_update"()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_TABLE_NAME = 'test_change_reviews' THEN
                        IF EXISTS (
                               SELECT 1
                               FROM "test_procedure_changes" source
                               JOIN "test_change_reviews" dependent ON dependent."OriginReferenceId" = source."Id"
                               WHERE source."TestChangeReviewId" = NEW."Id"
                                 AND dependent."OriginKind" = 'CaseChange'
                                 AND dependent."ArtifactKind" = 'Procedure'
                           ) THEN
                            IF NEW."State" NOT IN ('Approved','Superseded')
                               OR (NEW."State" IS DISTINCT FROM OLD."State"
                                   AND NOT (OLD."State" = 'Approved' AND NEW."State" = 'Superseded'))
                               OR NEW."ProjectId" IS DISTINCT FROM OLD."ProjectId"
                               OR NEW."ReleaseId" IS DISTINCT FROM OLD."ReleaseId"
                               OR NEW."Discipline" IS DISTINCT FROM OLD."Discipline"
                               OR NEW."ArtifactKind" IS DISTINCT FROM OLD."ArtifactKind"
                               OR NEW."BaseNumber" IS DISTINCT FROM OLD."BaseNumber"
                               OR NEW."Revision" IS DISTINCT FROM OLD."Revision"
                               OR NEW."ChangeRequestId" IS DISTINCT FROM OLD."ChangeRequestId" THEN
                                RAISE EXCEPTION 'A Case change that is a Procedure origin cannot be reopened or made ineligible';
                            END IF;
                        ELSIF EXISTS (
                               SELECT 1
                               FROM "verification_impact_items" item
                               JOIN "test_change_reviews" dependent ON dependent."OriginReferenceId" = item."Id"
                               WHERE item."TestChangeReviewId" = NEW."Id"
                                 AND dependent."OriginKind" = 'CaseAssessment'
                                 AND dependent."ArtifactKind" = 'Procedure'
                           ) THEN
                            IF (NEW."State" = 'Superseded' AND OLD."State" <> 'Approved')
                               OR (OLD."State" = 'Superseded' AND NEW."State" <> 'Superseded')
                               OR NEW."ProjectId" IS DISTINCT FROM OLD."ProjectId"
                               OR NEW."ReleaseId" IS DISTINCT FROM OLD."ReleaseId"
                               OR NEW."Discipline" IS DISTINCT FROM OLD."Discipline"
                               OR NEW."ArtifactKind" IS DISTINCT FROM OLD."ArtifactKind"
                               OR NEW."BaseNumber" IS DISTINCT FROM OLD."BaseNumber"
                               OR NEW."Revision" IS DISTINCT FROM OLD."Revision"
                               OR NEW."ChangeRequestId" IS DISTINCT FROM OLD."ChangeRequestId" THEN
                                RAISE EXCEPTION 'A Case assessment source package cannot be reopened or made ineligible';
                            END IF;
                        END IF;
                    ELSIF TG_TABLE_NAME = 'test_procedure_changes' THEN
                        IF (NEW."TestChangeReviewId" IS DISTINCT FROM OLD."TestChangeReviewId"
                            OR NEW."BaseNumber" IS DISTINCT FROM OLD."BaseNumber"
                            OR NEW."Revision" IS DISTINCT FROM OLD."Revision")
                           AND EXISTS (
                               SELECT 1 FROM "test_change_reviews"
                               WHERE "OriginKind" = 'CaseChange'
                                 AND "ArtifactKind" = 'Procedure'
                                 AND "OriginReferenceId" = OLD."Id"
                           ) THEN
                            RAISE EXCEPTION 'A Case change identity referenced by a Procedure package is immutable';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'verification_impact_items' THEN
                        IF (NEW."ProjectId" IS DISTINCT FROM OLD."ProjectId"
                            OR NEW."ReleaseId" IS DISTINCT FROM OLD."ReleaseId"
                            OR NEW."TestChangeReviewId" IS DISTINCT FROM OLD."TestChangeReviewId"
                            OR NEW."RequirementChangeId" IS DISTINCT FROM OLD."RequirementChangeId"
                            OR NEW."RequirementRevisionId" IS DISTINCT FROM OLD."RequirementRevisionId"
                            OR NEW."SubjectDisplayNumber" IS DISTINCT FROM OLD."SubjectDisplayNumber"
                            OR NEW."State" IS DISTINCT FROM OLD."State"
                            OR NEW."Outcome" IS DISTINCT FROM OLD."Outcome"
                            OR NEW."ProcedureChangeAction" IS DISTINCT FROM OLD."ProcedureChangeAction")
                           AND EXISTS (
                               SELECT 1 FROM "test_change_reviews"
                               WHERE "OriginKind" = 'CaseAssessment'
                                 AND "ArtifactKind" = 'Procedure'
                                 AND "OriginReferenceId" = OLD."Id"
                           ) THEN
                            RAISE EXCEPTION 'A Case assessment referenced by a Procedure package is immutable';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER "trg_refuse_test_change_review_case_source_update"
                    BEFORE UPDATE ON "test_change_reviews"
                    FOR EACH ROW EXECUTE FUNCTION "fn_refuse_test_change_review_case_source_update"();
                CREATE TRIGGER "trg_refuse_test_procedure_change_origin_update"
                    BEFORE UPDATE ON "test_procedure_changes"
                    FOR EACH ROW EXECUTE FUNCTION "fn_refuse_test_change_review_case_source_update"();
                CREATE TRIGGER "trg_refuse_verification_impact_origin_update"
                    BEFORE UPDATE ON "verification_impact_items"
                    FOR EACH ROW EXECUTE FUNCTION "fn_refuse_test_change_review_case_source_update"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_test_change_reviews_ChangeRequestId_Discipline_ArtifactKind~",
                table: "test_change_reviews");

            migrationBuilder.DropIndex(
                name: "IX_test_change_reviews_OriginKind_OriginReferenceId_Discipline_ArtifactKind_Revision",
                table: "test_change_reviews");

            migrationBuilder.DropCheckConstraint(
                name: "CK_test_change_reviews_origin_xor",
                table: "test_change_reviews");

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "trg_validate_test_change_review_case_origin" ON "test_change_reviews";
                DROP TRIGGER IF EXISTS "trg_refuse_test_procedure_change_origin_delete" ON "test_procedure_changes";
                DROP TRIGGER IF EXISTS "trg_refuse_verification_impact_origin_delete" ON "verification_impact_items";
                DROP TRIGGER IF EXISTS "trg_refuse_test_change_review_case_source_update" ON "test_change_reviews";
                DROP TRIGGER IF EXISTS "trg_refuse_test_procedure_change_origin_update" ON "test_procedure_changes";
                DROP TRIGGER IF EXISTS "trg_refuse_verification_impact_origin_update" ON "verification_impact_items";
                DROP FUNCTION IF EXISTS "fn_validate_test_change_review_case_origin"();
                DROP FUNCTION IF EXISTS "fn_refuse_test_change_review_case_origin_delete"();
                DROP FUNCTION IF EXISTS "fn_refuse_test_change_review_case_source_update"();
                """);

            migrationBuilder.DropColumn(
                name: "ArtifactKind",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "OriginKind",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "OriginReferenceId",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "SourceCaseOriginNumber",
                table: "test_change_reviews");

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_ChangeRequestId_Discipline_Revision",
                table: "test_change_reviews",
                columns: new[] { "ChangeRequestId", "Discipline", "Revision" },
                unique: true);
        }
    }
}
