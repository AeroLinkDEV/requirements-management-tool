using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GovernManagedDocumentRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_managed_document_links_RevisionId_ArtifactType_ArtifactId_R~",
                table: "managed_document_links");

            migrationBuilder.AddColumn<int>(
                name: "RelationshipManifestVersion",
                table: "managed_document_revisions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SubmittedRelationshipManifest",
                table: "managed_document_revisions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SubmittedRelationshipManifestHash",
                table: "managed_document_revisions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CanonicalTitle",
                table: "managed_document_links",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DeepLink",
                table: "managed_document_links",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrent",
                table: "managed_document_links",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PolicyVersion",
                table: "managed_document_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "managed_document_links",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SupersedeReason",
                table: "managed_document_links",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAt",
                table: "managed_document_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupersededBy",
                table: "managed_document_links",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByLinkId",
                table: "managed_document_links",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetProjectId",
                table: "managed_document_links",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TargetReleaseId",
                table: "managed_document_links",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetReleaseVersion",
                table: "managed_document_links",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TargetState",
                table: "managed_document_links",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            // Existing links predate canonical server resolution. Preserve their exact stored labels and meanings,
            // mark that limitation truthfully, and recover only provenance which the existing foreign keys prove.
            // Historical review/signature hashes remain unchanged: no old relationship manifest is fabricated.
            migrationBuilder.Sql("""
                UPDATE managed_document_links
                SET "IsCurrent" = TRUE,
                    "Provenance" = 'LegacyClientSupplied',
                    "TargetProjectId" = (
                        SELECT d."ProjectId"
                        FROM managed_document_revisions r
                        JOIN managed_documents d ON d."Id" = r."DocumentId"
                        WHERE r."Id" = managed_document_links."RevisionId"
                    ),
                    "TargetReleaseId" = CASE
                        WHEN "ArtifactType" = 'ChangeRequest' THEN (SELECT c."TargetReleaseId" FROM system_change_requests c WHERE c."Id" = managed_document_links."ArtifactId")
                        WHEN "ArtifactType" = 'TestChangeRequest' THEN (SELECT t."ReleaseId" FROM test_change_reviews t WHERE t."Id" = managed_document_links."ArtifactId")
                        WHEN "ArtifactType" = 'ProblemReport' THEN (SELECT p."TargetReleaseId" FROM problem_reports p WHERE p."Id" = managed_document_links."ArtifactId")
                        WHEN "ArtifactType" = 'Release' THEN "ArtifactId"
                        ELSE NULL
                    END
                """);

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_links_RevisionId_ArtifactType_ArtifactId_R~",
                table: "managed_document_links",
                columns: new[] { "RevisionId", "ArtifactType", "ArtifactId", "Relationship" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_links_RevisionId_IsCurrent",
                table: "managed_document_links",
                columns: new[] { "RevisionId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_links_SupersededByLinkId",
                table: "managed_document_links",
                column: "SupersededByLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_links_TargetProjectId",
                table: "managed_document_links",
                column: "TargetProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_links_TargetReleaseId",
                table: "managed_document_links",
                column: "TargetReleaseId");

            migrationBuilder.AddForeignKey(
                name: "FK_managed_document_links_managed_document_links_SupersededByL~",
                table: "managed_document_links",
                column: "SupersededByLinkId",
                principalTable: "managed_document_links",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_managed_document_links_projects_TargetProjectId",
                table: "managed_document_links",
                column: "TargetProjectId",
                principalTable: "projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_managed_document_links_software_releases_TargetReleaseId",
                table: "managed_document_links",
                column: "TargetReleaseId",
                principalTable: "software_releases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_managed_document_links_managed_document_links_SupersededByL~",
                table: "managed_document_links");

            migrationBuilder.DropForeignKey(
                name: "FK_managed_document_links_projects_TargetProjectId",
                table: "managed_document_links");

            migrationBuilder.DropForeignKey(
                name: "FK_managed_document_links_software_releases_TargetReleaseId",
                table: "managed_document_links");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_links_RevisionId_ArtifactType_ArtifactId_R~",
                table: "managed_document_links");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_links_RevisionId_IsCurrent",
                table: "managed_document_links");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_links_SupersededByLinkId",
                table: "managed_document_links");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_links_TargetProjectId",
                table: "managed_document_links");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_links_TargetReleaseId",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "RelationshipManifestVersion",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "SubmittedRelationshipManifest",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "SubmittedRelationshipManifestHash",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "CanonicalTitle",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "DeepLink",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "IsCurrent",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "PolicyVersion",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "Provenance",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "SupersedeReason",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "SupersededBy",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "SupersededByLinkId",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "TargetProjectId",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "TargetReleaseId",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "TargetReleaseVersion",
                table: "managed_document_links");

            migrationBuilder.DropColumn(
                name: "TargetState",
                table: "managed_document_links");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_links_RevisionId_ArtifactType_ArtifactId_R~",
                table: "managed_document_links",
                columns: new[] { "RevisionId", "ArtifactType", "ArtifactId", "Relationship" },
                unique: true);
        }
    }
}
