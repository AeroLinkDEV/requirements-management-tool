using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SealProjectLadder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_project_ladder_configuration_state",
                table: "project_ladder_configurations");

            migrationBuilder.AddColumn<bool>(
                name: "IsSealed",
                table: "project_ladder_configurations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUpgradeAt",
                table: "project_ladder_configurations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpgradeBy",
                table: "project_ladder_configurations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpgradeManifestHash",
                table: "project_ladder_configurations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpgradeVersion",
                table: "project_ladder_configurations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SealedAt",
                table: "project_ladder_configurations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SealedBy",
                table: "project_ladder_configurations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SealedContentIdentity",
                table: "project_ladder_configurations",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SealedContentKind",
                table: "project_ladder_configurations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Existing installations already contain controlled records written before the seal boundary existed.
            // Use one deterministic migration-owned dependency summary per project. It intentionally does not
            // claim that the migration can identify which historical row was first, so timestamp/kind/identity/
            // actor cannot accidentally describe different rows.
            migrationBuilder.Sql("""
                UPDATE "project_ladder_configurations" AS c
                SET "IsSealed" = TRUE,
                    "SealedAt" = CURRENT_TIMESTAMP,
                    "SealedBy" = 'migration.backfill',
                    "SealedContentKind" = 'migration-backfill',
                    "SealedContentIdentity" = 'project:' || CAST(c."ProjectId" AS TEXT) || ':existing-controlled-content',
                    "Version" = c."Version" + 1
                WHERE EXISTS (SELECT 1 FROM "requirements" r WHERE r."ProjectId" = c."ProjectId")
                   OR EXISTS (SELECT 1 FROM "system_change_requests" scr JOIN "requirement_changes" rc ON rc."ChangeRequestId" = scr."Id" WHERE scr."ProjectId" = c."ProjectId")
                   OR EXISTS (SELECT 1 FROM "test_procedures" tp WHERE tp."ProjectId" = c."ProjectId")
                   OR EXISTS (SELECT 1 FROM "test_change_reviews" tr WHERE tr."ProjectId" = c."ProjectId")
                   OR EXISTS (SELECT 1 FROM "requirement_trace_links" tl WHERE tl."ProjectId" = c."ProjectId")
                   OR EXISTS (SELECT 1 FROM "code_traceability_records" ct WHERE ct."ProjectId" = c."ProjectId");
                """);

            // Preserve an immutable, migration-owned evidence boundary for every configuration backfilled above.
            // Non-default configurations must already have a real authoring/activation history snapshot. The
            // migration refuses to invent one; the fixed legacy snapshot is the exact persisted default graph.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "project_ladder_configurations" AS c
                        WHERE c."IsSealed" = TRUE
                          AND c."SealedContentKind" = 'migration-backfill'
                          AND NOT (c."Classification" = 'LegacyDefault' AND c."State" = 'Stored')
                          AND NOT EXISTS (
                              SELECT 1 FROM "project_ladder_configuration_history" AS h
                              WHERE h."ConfigurationId" = c."Id"
                          )
                    ) THEN
                        RAISE EXCEPTION 'Cannot backfill a non-default ladder without an existing real history snapshot';
                    END IF;
                END $$;
                WITH backfilled AS (
                    SELECT c."Id", c."ProjectId", c."SealedAt", c."Version" AS "Revision",
                           COALESCE(latest."CanonicalSnapshot",
                               'steps[1:System:7;2:HighLevel:7;3:LowLevel:15]|edges[HighLevel>LowLevel;System>HighLevel]') AS "CanonicalSnapshot",
                           COALESCE(latest."SnapshotHash",
                               '6fc44a4303eee5204f376a377bf139da11c421ca35e3d64b9b15cadcdb502fb7') AS "SnapshotHash"
                    FROM "project_ladder_configurations" AS c
                    LEFT JOIN LATERAL (
                        SELECT h."CanonicalSnapshot", h."SnapshotHash"
                        FROM "project_ladder_configuration_history" AS h
                        WHERE h."ConfigurationId" = c."Id"
                        ORDER BY h."Revision" DESC
                        LIMIT 1
                    ) AS latest ON TRUE
                    WHERE c."IsSealed" = TRUE
                      AND c."SealedContentKind" = 'migration-backfill'
                )
                INSERT INTO "project_ladder_configuration_history"
                    ("Id", "ConfigurationId", "ProjectId", "Revision", "Actor", "OccurredAt", "Reason", "CanonicalSnapshot", "SnapshotHash")
                SELECT md5(g."Id"::text || ':migration-backfill:' || g."Revision"::text)::uuid,
                       g."Id", g."ProjectId", g."Revision", 'migration.backfill', g."SealedAt",
                       'Migration-owned seal boundary for existing controlled content; historical first content is not inferred.',
                       g."CanonicalSnapshot", g."SnapshotHash"
                FROM backfilled AS g
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM "project_ladder_configuration_history" AS existing
                    WHERE existing."ConfigurationId" = g."Id"
                      AND existing."Revision" = g."Revision"
                )
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_project_ladder_configuration_state",
                table: "project_ladder_configurations",
                sql: "((\"Classification\" = 'LegacyDefault' AND \"State\" = 'Stored' AND \"ActivatedAt\" IS NULL AND \"ActivatedBy\" IS NULL AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL AND \"ActivationManifestVersion\" IS NULL AND \"ActivationManifestHash\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Draft' AND \"ActivatedAt\" IS NULL AND \"ActivatedBy\" IS NULL AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL AND \"ActivationManifestVersion\" IS NULL AND \"ActivationManifestHash\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Active' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedBy\" IS NOT NULL AND length(trim(\"ActivatedBy\")) > 0 AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL AND \"ActivationManifestVersion\" IS NOT NULL AND length(trim(\"ActivationManifestVersion\")) > 0 AND \"ActivationManifestHash\" IS NOT NULL AND length(trim(\"ActivationManifestHash\")) > 0) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Retired' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedBy\" IS NOT NULL AND length(trim(\"ActivatedBy\")) > 0 AND \"RetiredAt\" IS NOT NULL AND \"RetiredBy\" IS NOT NULL AND length(trim(\"RetiredBy\")) > 0 AND \"ActivationManifestVersion\" IS NOT NULL AND length(trim(\"ActivationManifestVersion\")) > 0 AND \"ActivationManifestHash\" IS NOT NULL AND length(trim(\"ActivationManifestHash\")) > 0)) AND ((\"IsSealed\" = FALSE AND \"SealedAt\" IS NULL AND \"SealedBy\" IS NULL AND \"SealedContentKind\" IS NULL AND \"SealedContentIdentity\" IS NULL AND \"LastUpgradeAt\" IS NULL AND \"LastUpgradeBy\" IS NULL AND \"LastUpgradeVersion\" IS NULL AND \"LastUpgradeManifestHash\" IS NULL) OR (\"IsSealed\" = TRUE AND \"SealedAt\" IS NOT NULL AND \"SealedBy\" IS NOT NULL AND length(trim(\"SealedBy\")) > 0 AND \"SealedContentKind\" IS NOT NULL AND length(trim(\"SealedContentKind\")) > 0 AND \"SealedContentIdentity\" IS NOT NULL AND length(trim(\"SealedContentIdentity\")) > 0 AND ((\"LastUpgradeAt\" IS NULL AND \"LastUpgradeBy\" IS NULL AND \"LastUpgradeVersion\" IS NULL AND \"LastUpgradeManifestHash\" IS NULL) OR (\"LastUpgradeAt\" IS NOT NULL AND \"LastUpgradeBy\" IS NOT NULL AND length(trim(\"LastUpgradeBy\")) > 0 AND \"LastUpgradeVersion\" IS NOT NULL AND length(trim(\"LastUpgradeVersion\")) > 0 AND \"LastUpgradeManifestHash\" IS NOT NULL AND length(trim(\"LastUpgradeManifestHash\")) > 0))))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_project_ladder_configuration_state",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "IsSealed",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "LastUpgradeAt",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "LastUpgradeBy",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "LastUpgradeManifestHash",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "LastUpgradeVersion",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "SealedAt",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "SealedBy",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "SealedContentIdentity",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "SealedContentKind",
                table: "project_ladder_configurations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_project_ladder_configuration_state",
                table: "project_ladder_configurations",
                sql: "((\"Classification\" = 'LegacyDefault' AND \"State\" = 'Stored' AND \"ActivatedAt\" IS NULL AND \"ActivatedBy\" IS NULL AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL AND \"ActivationManifestVersion\" IS NULL AND \"ActivationManifestHash\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Draft' AND \"ActivatedAt\" IS NULL AND \"ActivatedBy\" IS NULL AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL AND \"ActivationManifestVersion\" IS NULL AND \"ActivationManifestHash\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Active' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedBy\" IS NOT NULL AND length(trim(\"ActivatedBy\")) > 0 AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL AND \"ActivationManifestVersion\" IS NOT NULL AND length(trim(\"ActivationManifestVersion\")) > 0 AND \"ActivationManifestHash\" IS NOT NULL AND length(trim(\"ActivationManifestHash\")) > 0) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Retired' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedBy\" IS NOT NULL AND length(trim(\"ActivatedBy\")) > 0 AND \"RetiredAt\" IS NOT NULL AND \"RetiredBy\" IS NOT NULL AND length(trim(\"RetiredBy\")) > 0 AND \"ActivationManifestVersion\" IS NOT NULL AND length(trim(\"ActivationManifestVersion\")) > 0 AND \"ActivationManifestHash\" IS NOT NULL AND length(trim(\"ActivationManifestHash\")) > 0))");
        }
    }
}
