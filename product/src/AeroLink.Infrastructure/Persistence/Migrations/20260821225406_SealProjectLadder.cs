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
                    "SealedAt" = c."CreatedAt",
                    "SealedBy" = 'migration.backfill',
                    "SealedContentKind" = 'migration-backfill',
                    "SealedContentIdentity" = 'project:' || CAST(c."ProjectId" AS TEXT) || ':existing-controlled-content'
                WHERE EXISTS (SELECT 1 FROM "requirements" r WHERE r."ProjectId" = c."ProjectId")
                   OR EXISTS (SELECT 1 FROM "system_change_requests" scr JOIN "requirement_changes" rc ON rc."ChangeRequestId" = scr."Id" WHERE scr."ProjectId" = c."ProjectId")
                   OR EXISTS (SELECT 1 FROM "test_procedures" tp WHERE tp."ProjectId" = c."ProjectId")
                   OR EXISTS (SELECT 1 FROM "test_change_reviews" tr WHERE tr."ProjectId" = c."ProjectId")
                   OR EXISTS (SELECT 1 FROM "requirement_trace_links" tl WHERE tl."ProjectId" = c."ProjectId")
                   OR EXISTS (SELECT 1 FROM "code_traceability_records" ct WHERE ct."ProjectId" = c."ProjectId");
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
