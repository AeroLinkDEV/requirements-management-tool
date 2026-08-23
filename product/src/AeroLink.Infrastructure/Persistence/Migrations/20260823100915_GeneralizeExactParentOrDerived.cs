using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeExactParentOrDerived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SnapshotContractVersion",
                table: "system_change_requests",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "DerivedRationale",
                table: "test_procedure_changes",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ParentKind",
                table: "test_procedure_changes",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Unspecified");

            migrationBuilder.AddColumn<string>(
                name: "ParentRevisionIdsJson",
                table: "test_procedure_changes",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "DerivedRationale",
                table: "requirement_revisions",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ParentKind",
                table: "requirement_revisions",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Unspecified");

            migrationBuilder.AddColumn<string>(
                name: "ParentRevisionIdsJson",
                table: "requirement_revisions",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            // Existing System Procedure and Case revisions with persisted exact requirement coverage have
            // honest Allocated evidence. Rows with no coverage remain Unspecified: neither mode nor rationale
            // can be fabricated from absence.
            migrationBuilder.Sql("""
                UPDATE test_procedure_revisions AS revision
                SET "ParentKind" = 'Allocated'
                FROM (
                    SELECT coverage."ProcedureRevisionId" AS revision_id
                    FROM test_requirement_coverage AS coverage
                    JOIN test_procedure_revisions AS covered_revision
                      ON covered_revision."Id" = coverage."ProcedureRevisionId"
                    JOIN test_procedures AS procedure
                      ON procedure."Id" = covered_revision."ProcedureId"
                    WHERE covered_revision."State" <> 'Retired'
                      AND coverage."IsSuspect" = FALSE
                      AND (procedure."Level" = 'System' OR procedure."ArtifactKind" = 'Case')
                    GROUP BY coverage."ProcedureRevisionId"
                ) AS classified
                WHERE revision."Id" = classified.revision_id
                  AND revision."ParentKind" = 'Unspecified'
            """);

            // Preserve only evidence that is already present. A persisted
            // AllocatedFrom trace is sufficient to classify a requirement
            // revision and its exact parent identities; a derived marker is
            // carried only where the authored profile actually says so.
            migrationBuilder.Sql("""
                UPDATE requirement_revisions AS revision
                SET "ParentKind" = 'Allocated',
                    "ParentRevisionIdsJson" = parents.ids
                FROM (
                    SELECT trace."SourceRevisionId" AS revision_id,
                           json_agg(trace."TargetRevisionId" ORDER BY trace."TargetRevisionId")::text AS ids
                    FROM requirement_trace_links AS trace
                    WHERE trace."Type" = 'AllocatedFrom'
                      AND trace."ExactLinkSuspectLifecycleId" IS NULL
                    GROUP BY trace."SourceRevisionId"
                ) AS parents
                WHERE revision."Id" = parents.revision_id
                  AND parents.ids IS NOT NULL
            """);
            migrationBuilder.Sql("""
                UPDATE requirement_revisions AS revision
                SET "ParentKind" = 'Derived',
                    "DerivedRationale" = revision."Rationale"
                WHERE revision."ParentKind" = 'Unspecified'
                  AND NULLIF(TRIM(revision."Rationale"), '') IS NOT NULL
                  AND EXISTS (
                      SELECT 1
                      FROM requirement_revision_profiles AS profile
                      WHERE profile."RevisionId" = revision."Id"
                        AND COALESCE((profile."AttributesJson"::jsonb ->> 'derived') = 'true', false)
                  )
            """);
            migrationBuilder.Sql("""
                UPDATE test_procedure_changes
                SET "ParentKind" = 'Allocated',
                    "ParentRevisionIdsJson" = (
                        SELECT jsonb_agg(value ORDER BY value)::text
                        FROM jsonb_array_elements_text("DrivingRequirementRevisionIdsJson"::jsonb) AS item(value)
                    )
                WHERE "Kind" = 'Introduce'
                  AND "DrivingRequirementRevisionIdsJson" IS NOT NULL
                  AND jsonb_typeof("DrivingRequirementRevisionIdsJson"::jsonb) = 'array'
                  AND "DrivingRequirementRevisionIdsJson"::jsonb <> '[]'::jsonb
            """);

            // A historical Modify driving list is only a delta and cannot establish the successor's full
            // selection by itself. When the exact successor was actually materialized, however, its coverage
            // rows are authoritative evidence. Use only an unambiguous non-retired successor for this
            // evidence-derived backfill; pending or ambiguous Modify proposals remain legacy Unspecified.
            migrationBuilder.Sql("""
                UPDATE test_procedure_changes AS change
                SET "ParentKind" = 'Allocated',
                    "ParentRevisionIdsJson" = materialized.parent_ids
                FROM (
                    SELECT review."Id" AS review_id,
                           procedure."ProjectId" AS project_id,
                           procedure."BaseNumber" AS base_number,
                           revision."Revision" AS revision_number,
                           json_agg(coverage."RequirementRevisionId" ORDER BY coverage."RequirementRevisionId")::text AS parent_ids,
                           COUNT(DISTINCT revision."Id") AS successor_count
                    FROM test_change_reviews AS review
                    JOIN test_procedure_revisions AS revision
                      ON revision."SourceTestChangeRequestId" = review."Id"
                    JOIN test_procedures AS procedure
                      ON procedure."Id" = revision."ProcedureId"
                     AND procedure."ProjectId" = review."ProjectId"
                    JOIN test_requirement_coverage AS coverage
                      ON coverage."ProcedureRevisionId" = revision."Id"
                    WHERE revision."State" <> 'Retired'
                      AND coverage."IsSuspect" = FALSE
                    GROUP BY review."Id", procedure."ProjectId", procedure."BaseNumber", revision."Revision"
                    HAVING COUNT(DISTINCT revision."Id") = 1
                ) AS materialized
                WHERE change."TestChangeReviewId" = materialized.review_id
                  AND change."BaseNumber" = materialized.base_number
                  AND change."Revision" = materialized.revision_number
                  AND change."Kind" = 'Modify'
                  AND change."ParentKind" = 'Unspecified'
                  AND materialized.successor_count = 1
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SnapshotContractVersion",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "DerivedRationale",
                table: "test_procedure_changes");

            migrationBuilder.DropColumn(
                name: "ParentKind",
                table: "test_procedure_changes");

            migrationBuilder.DropColumn(
                name: "ParentRevisionIdsJson",
                table: "test_procedure_changes");

            migrationBuilder.DropColumn(
                name: "DerivedRationale",
                table: "requirement_revisions");

            migrationBuilder.DropColumn(
                name: "ParentKind",
                table: "requirement_revisions");

            migrationBuilder.DropColumn(
                name: "ParentRevisionIdsJson",
                table: "requirement_revisions");
        }
    }
}
