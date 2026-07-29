using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTestChangeReviewsAndCanonicalBuildNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The product deliberately adopts the new identifiers as canonical, including historical rows.
            // Foreign keys use GUIDs, so changing the controlled display number does not disturb relationships.
            migrationBuilder.Sql("""
                UPDATE system_change_requests
                   SET "BaseNumber" =
                       CASE
                         WHEN "BaseNumber" LIKE 'SCR-________' THEN substr("BaseNumber", 1, 4) || substr("BaseNumber", 8)
                         WHEN "BaseNumber" LIKE 'SWCR-________' THEN substr("BaseNumber", 1, 5) || substr("BaseNumber", 9)
                         ELSE "BaseNumber"
                       END
                 WHERE "BaseNumber" LIKE 'SCR-________' OR "BaseNumber" LIKE 'SWCR-________';
                """);

            // Stored snapshots and outward-facing references carry the number as text rather than a GUID.
            // This migration intentionally rewrites them too: there is no legacy alias in the new product rule.
            foreach (var statement in new[]
            {
                """UPDATE baseline_scr_selections SET "ScrDisplayNumber" = replace(replace("ScrDisplayNumber", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-');""",
                """UPDATE audit_events SET "Detail" = replace(replace("Detail", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-'), "EvidenceJson" = replace(replace("EvidenceJson", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-') WHERE "Detail" LIKE '%CR-000%' OR "EvidenceJson" LIKE '%CR-000%';""",
                """UPDATE baseline_events SET "Detail" = replace(replace("Detail", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-') WHERE "Detail" LIKE '%CR-000%';""",
                """UPDATE electronic_signatures SET "ArtifactRevision" = replace(replace("ArtifactRevision", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-'), "Meaning" = replace(replace("Meaning", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-') WHERE "ArtifactRevision" LIKE '%CR-000%' OR "Meaning" LIKE '%CR-000%';""",
                """UPDATE security_audit_events SET "Target" = replace(replace("Target", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-'), "Detail" = replace(replace("Detail", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-') WHERE "Target" LIKE '%CR-000%' OR "Detail" LIKE '%CR-000%';""",
                """UPDATE user_notifications SET "Title" = replace(replace("Title", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-'), "Detail" = replace(replace("Detail", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-') WHERE "Title" LIKE '%CR-000%' OR "Detail" LIKE '%CR-000%';""",
                """UPDATE jira_issue_links SET "ArtifactNumber" = replace(replace("ArtifactNumber", 'SWCR-000', 'SWCR-'), 'SCR-000', 'SCR-') WHERE "ArtifactNumber" LIKE '%CR-000%';"""
            }) migrationBuilder.Sql(statement);

            // FMS software-build names are now the official baseline identifiers.
            migrationBuilder.Sql("""
                UPDATE candidate_baselines SET "BaseNumber" = 'SW-01.50', "Name" = 'FMS 1.5 Released Software Build'
                 WHERE "BaseNumber" = 'SWBL-00000015';
                UPDATE candidate_baselines SET "BaseNumber" = 'SW-01.60', "Name" = 'FMS 1.6 In-Work Software Build'
                 WHERE "BaseNumber" = 'SWBL-00000016';
                UPDATE software_builds SET "BuildNumber" = 'SW-01.50'
                 WHERE "BaselineId" IN (SELECT "Id" FROM candidate_baselines WHERE "BaseNumber" = 'SW-01.50');
                UPDATE software_builds SET "BuildNumber" = 'SW-01.60'
                 WHERE "BaselineId" IN (SELECT "Id" FROM candidate_baselines WHERE "BaseNumber" = 'SW-01.60');
                UPDATE baseline_events SET "Detail" = replace(replace("Detail", 'SWBL-00000015.00', 'SW-01.50'), 'SWBL-00000016.00', 'SW-01.60')
                 WHERE "Detail" LIKE '%SWBL-0000001%';
                """);

            migrationBuilder.AddColumn<bool>(
                name: "PreReleaseEvidenceRequired",
                table: "verification_impact_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProcedureChangeAction",
                table: "verification_impact_items",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TestChangeReviewId",
                table: "verification_impact_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "test_change_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Discipline = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceChangeRequestNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AssignedEngineerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SubmittedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovalRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_change_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_change_reviews_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_change_reviews_software_releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_change_reviews_system_change_requests_ChangeRequestId",
                        column: x => x.ChangeRequestId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    WITH sources AS (
                        SELECT vi."Id", vi."ProjectId", vi."ReleaseId", vi."ChangeRequestId",
                               CASE
                                 WHEN rc."Level" = 'System' OR tp."Level" = 'System' THEN 'System'
                                 WHEN rc."Level" = 'HighLevel' OR tp."Level" = 'HighLevel' THEN 'HighLevelSoftware'
                                 ELSE 'LowLevelSoftware'
                               END AS discipline,
                               scr."BaseNumber" || '.' || lpad(scr."Revision"::text, 2, '0') AS source_number,
                               CASE WHEN bool_and(vi."State" = 'Resolved') OVER (
                                   PARTITION BY vi."ChangeRequestId",
                                   CASE
                                     WHEN rc."Level" = 'System' OR tp."Level" = 'System' THEN 'System'
                                     WHEN rc."Level" = 'HighLevel' OR tp."Level" = 'HighLevel' THEN 'HighLevelSoftware'
                                     ELSE 'LowLevelSoftware'
                                   END) THEN 'Approved' ELSE 'Open' END AS review_state,
                               row_number() OVER (
                                   PARTITION BY vi."ChangeRequestId",
                                   CASE
                                     WHEN rc."Level" = 'System' OR tp."Level" = 'System' THEN 'System'
                                     WHEN rc."Level" = 'HighLevel' OR tp."Level" = 'HighLevel' THEN 'HighLevelSoftware'
                                     ELSE 'LowLevelSoftware'
                                   END ORDER BY vi."RaisedAt", vi."Id") AS rn,
                               min(vi."RaisedAt") OVER (PARTITION BY vi."ChangeRequestId") AS created_at,
                               max(vi."UpdatedAt") OVER (PARTITION BY vi."ChangeRequestId") AS updated_at
                          FROM verification_impact_items vi
                          JOIN system_change_requests scr ON scr."Id" = vi."ChangeRequestId"
                          LEFT JOIN requirement_changes rc ON rc."Id" = vi."RequirementChangeId"
                          LEFT JOIN test_procedures tp ON tp."Id" = vi."ProcedureId"
                    )
                    INSERT INTO test_change_reviews
                        ("Id","ProjectId","ReleaseId","ChangeRequestId","Discipline","SourceChangeRequestNumber",
                         "State","ApprovalRationale","CreatedAt","UpdatedAt","Version")
                    SELECT "Id","ProjectId","ReleaseId","ChangeRequestId",discipline,source_number,review_state,
                           CASE WHEN review_state = 'Approved' THEN 'Migrated completed verification decisions.' ELSE '' END,
                           created_at,updated_at,1
                      FROM sources WHERE rn = 1;

                    UPDATE verification_impact_items vi
                       SET "TestChangeReviewId" = review."Id"
                      FROM test_change_reviews review
                     WHERE review."ChangeRequestId" = vi."ChangeRequestId"
                       AND review."Discipline" =
                           CASE
                             WHEN (SELECT rc."Level" FROM requirement_changes rc WHERE rc."Id" = vi."RequirementChangeId") = 'System'
                               OR (SELECT tp."Level" FROM test_procedures tp WHERE tp."Id" = vi."ProcedureId") = 'System'
                               THEN 'System'
                             WHEN (SELECT rc."Level" FROM requirement_changes rc WHERE rc."Id" = vi."RequirementChangeId") = 'HighLevel'
                               OR (SELECT tp."Level" FROM test_procedures tp WHERE tp."Id" = vi."ProcedureId") = 'HighLevel'
                               THEN 'HighLevelSoftware'
                             ELSE 'LowLevelSoftware'
                           END;
                    """);
            }
            else
            {
                migrationBuilder.Sql("""
                    WITH sources AS (
                        SELECT vi."Id", vi."ProjectId", vi."ReleaseId", vi."ChangeRequestId",
                               CASE
                                 WHEN rc."Level" = 'System' OR tp."Level" = 'System' THEN 'System'
                                 WHEN rc."Level" = 'HighLevel' OR tp."Level" = 'HighLevel' THEN 'HighLevelSoftware'
                                 ELSE 'LowLevelSoftware'
                               END AS discipline,
                               scr."BaseNumber" || '.' || printf('%02d', scr."Revision") AS source_number,
                               CASE WHEN sum(CASE WHEN vi."State" <> 'Resolved' THEN 1 ELSE 0 END) OVER (
                                   PARTITION BY vi."ChangeRequestId",
                                   CASE
                                     WHEN rc."Level" = 'System' OR tp."Level" = 'System' THEN 'System'
                                     WHEN rc."Level" = 'HighLevel' OR tp."Level" = 'HighLevel' THEN 'HighLevelSoftware'
                                     ELSE 'LowLevelSoftware'
                                   END) = 0 THEN 'Approved' ELSE 'Open' END AS review_state,
                               row_number() OVER (
                                   PARTITION BY vi."ChangeRequestId",
                                   CASE
                                     WHEN rc."Level" = 'System' OR tp."Level" = 'System' THEN 'System'
                                     WHEN rc."Level" = 'HighLevel' OR tp."Level" = 'HighLevel' THEN 'HighLevelSoftware'
                                     ELSE 'LowLevelSoftware'
                                   END ORDER BY vi."RaisedAt", vi."Id") AS rn,
                               min(vi."RaisedAt") OVER (PARTITION BY vi."ChangeRequestId") AS created_at,
                               max(vi."UpdatedAt") OVER (PARTITION BY vi."ChangeRequestId") AS updated_at
                          FROM verification_impact_items vi
                          JOIN system_change_requests scr ON scr."Id" = vi."ChangeRequestId"
                          LEFT JOIN requirement_changes rc ON rc."Id" = vi."RequirementChangeId"
                          LEFT JOIN test_procedures tp ON tp."Id" = vi."ProcedureId"
                    )
                    INSERT INTO test_change_reviews
                        ("Id","ProjectId","ReleaseId","ChangeRequestId","Discipline","SourceChangeRequestNumber",
                         "State","ApprovalRationale","CreatedAt","UpdatedAt","Version")
                    SELECT "Id","ProjectId","ReleaseId","ChangeRequestId",discipline,source_number,review_state,
                           CASE WHEN review_state = 'Approved' THEN 'Migrated completed verification decisions.' ELSE '' END,
                           created_at,updated_at,1
                      FROM sources WHERE rn = 1;

                    UPDATE verification_impact_items
                       SET "TestChangeReviewId" = (
                           SELECT review."Id"
                             FROM test_change_reviews review
                             LEFT JOIN requirement_changes rc ON rc."Id" = verification_impact_items."RequirementChangeId"
                             LEFT JOIN test_procedures tp ON tp."Id" = verification_impact_items."ProcedureId"
                            WHERE review."ChangeRequestId" = verification_impact_items."ChangeRequestId"
                              AND review."Discipline" =
                                  CASE
                                    WHEN rc."Level" = 'System' OR tp."Level" = 'System' THEN 'System'
                                    WHEN rc."Level" = 'HighLevel' OR tp."Level" = 'HighLevel' THEN 'HighLevelSoftware'
                                    ELSE 'LowLevelSoftware'
                                  END
                       );
                    """);
            }

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_items_TestChangeReviewId",
                table: "verification_impact_items",
                column: "TestChangeReviewId");

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_ChangeRequestId_Discipline",
                table: "test_change_reviews",
                columns: new[] { "ChangeRequestId", "Discipline" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_ProjectId",
                table: "test_change_reviews",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_ReleaseId_State_Discipline",
                table: "test_change_reviews",
                columns: new[] { "ReleaseId", "State", "Discipline" });

            migrationBuilder.AddForeignKey(
                name: "FK_verification_impact_items_test_change_reviews_TestChangeRev~",
                table: "verification_impact_items",
                column: "TestChangeReviewId",
                principalTable: "test_change_reviews",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_verification_impact_items_test_change_reviews_TestChangeRev~",
                table: "verification_impact_items");

            migrationBuilder.DropTable(
                name: "test_change_reviews");

            migrationBuilder.DropIndex(
                name: "IX_verification_impact_items_TestChangeReviewId",
                table: "verification_impact_items");

            migrationBuilder.DropColumn(
                name: "PreReleaseEvidenceRequired",
                table: "verification_impact_items");

            migrationBuilder.DropColumn(
                name: "ProcedureChangeAction",
                table: "verification_impact_items");

            migrationBuilder.DropColumn(
                name: "TestChangeReviewId",
                table: "verification_impact_items");
        }
    }
}
