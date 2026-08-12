using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DecoupleManagedDocumentsFromBuilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_managed_document_revisions_software_releases_TargetReleaseId",
                table: "managed_document_revisions");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_revisions_TargetReleaseId_State",
                table: "managed_document_revisions");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentReleasedDocxAttachmentId",
                table: "managed_document_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentReleasedDocxSha256",
                table: "managed_document_revisions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentRevisionId",
                table: "managed_document_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransformationProfile",
                table: "managed_document_revisions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "managed_document_build_provenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_document_build_provenance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_document_build_provenance_managed_document_revision~",
                        column: x => x.RevisionId,
                        principalTable: "managed_document_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_build_provenance_managed_documents_Documen~",
                        column: x => x.DocumentId,
                        principalTable: "managed_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_build_provenance_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_build_provenance_software_releases_Release~",
                        column: x => x.ReleaseId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                INSERT INTO managed_document_build_provenance
                    ("Id", "ProjectId", "ReleaseId", "DocumentId", "RevisionId", "Source", "RecordedBy", "RecordedAt")
                SELECT "Id", "ProjectId", "ReleaseId", "DocumentId", "RevisionId",
                       'LegacyBuildSelection', "SelectedBy", "SelectedAt"
                FROM managed_document_build_selections;

                INSERT INTO managed_document_build_provenance
                    ("Id", "ProjectId", "ReleaseId", "DocumentId", "RevisionId", "Source", "RecordedBy", "RecordedAt")
                SELECT gen_random_uuid(), d."ProjectId", r."TargetReleaseId", r."DocumentId", r."Id",
                       'LegacyTargetRelease', COALESCE(r."SubmittedBy", r."OwnerId"), r."CreatedAt"
                FROM managed_document_revisions r
                JOIN managed_documents d ON d."Id" = r."DocumentId";

                UPDATE managed_document_revisions child
                SET "ParentRevisionId" = parent."Id",
                    "ParentReleasedDocxAttachmentId" = parent."ReleasedDocxAttachmentId",
                    "ParentReleasedDocxSha256" = attachment."Sha256",
                    "TransformationProfile" = 'legacy-lineage-import-v1'
                FROM managed_document_revisions parent
                JOIN controlled_attachments attachment ON attachment."Id" = parent."ReleasedDocxAttachmentId"
                WHERE child."DocumentId" = parent."DocumentId"
                  AND child."Revision" = parent."Revision" + 1
                  AND child."Revision" > 0;
                """);

            migrationBuilder.DropTable(
                name: "managed_document_build_selections");

            migrationBuilder.DropColumn(
                name: "TargetReleaseId",
                table: "managed_document_revisions");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_revisions_DocumentId_State",
                table: "managed_document_revisions",
                columns: new[] { "DocumentId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_revisions_ParentReleasedDocxAttachmentId",
                table: "managed_document_revisions",
                column: "ParentReleasedDocxAttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_revisions_ParentRevisionId",
                table: "managed_document_revisions",
                column: "ParentRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_build_provenance_DocumentId_ReleaseId_Revi~",
                table: "managed_document_build_provenance",
                columns: new[] { "DocumentId", "ReleaseId", "RevisionId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_build_provenance_ProjectId",
                table: "managed_document_build_provenance",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_build_provenance_ReleaseId",
                table: "managed_document_build_provenance",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_build_provenance_RevisionId",
                table: "managed_document_build_provenance",
                column: "RevisionId");

            migrationBuilder.AddForeignKey(
                name: "FK_managed_document_revisions_controlled_attachments_ParentRel~",
                table: "managed_document_revisions",
                column: "ParentReleasedDocxAttachmentId",
                principalTable: "controlled_attachments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_managed_document_revisions_managed_document_revisions_Paren~",
                table: "managed_document_revisions",
                column: "ParentRevisionId",
                principalTable: "managed_document_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_managed_document_revisions_controlled_attachments_ParentRel~",
                table: "managed_document_revisions");

            migrationBuilder.DropForeignKey(
                name: "FK_managed_document_revisions_managed_document_revisions_Paren~",
                table: "managed_document_revisions");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_revisions_DocumentId_State",
                table: "managed_document_revisions");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_revisions_ParentReleasedDocxAttachmentId",
                table: "managed_document_revisions");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_revisions_ParentRevisionId",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "ParentReleasedDocxAttachmentId",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "ParentReleasedDocxSha256",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "ParentRevisionId",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "TransformationProfile",
                table: "managed_document_revisions");

            migrationBuilder.AddColumn<Guid>(
                name: "TargetReleaseId",
                table: "managed_document_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "managed_document_build_selections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SelectedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_document_build_selections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_document_build_selections_managed_document_revision~",
                        column: x => x.RevisionId,
                        principalTable: "managed_document_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_build_selections_managed_documents_Documen~",
                        column: x => x.DocumentId,
                        principalTable: "managed_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_build_selections_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_build_selections_software_releases_Release~",
                        column: x => x.ReleaseId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM managed_document_revisions r
                        WHERE NOT EXISTS (
                            SELECT 1
                            FROM managed_document_build_provenance p
                            WHERE p."RevisionId" = r."Id"
                              AND p."Source" = 'LegacyTargetRelease'))
                    THEN
                        RAISE EXCEPTION 'Cannot downgrade managed documents: Project-wide revisions have no truthful legacy TargetReleaseId.';
                    END IF;
                END $$;

                UPDATE managed_document_revisions r
                SET "TargetReleaseId" = p."ReleaseId"
                FROM managed_document_build_provenance p
                WHERE p."RevisionId" = r."Id"
                  AND p."Source" = 'LegacyTargetRelease';

                INSERT INTO managed_document_build_selections
                    ("Id", "ProjectId", "ReleaseId", "DocumentId", "RevisionId", "SelectedBy", "SelectedAt")
                SELECT "Id", "ProjectId", "ReleaseId", "DocumentId", "RevisionId", "RecordedBy", "RecordedAt"
                FROM managed_document_build_provenance
                WHERE "Source" = 'LegacyBuildSelection';
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "TargetReleaseId",
                table: "managed_document_revisions",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropTable(
                name: "managed_document_build_provenance");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_revisions_TargetReleaseId_State",
                table: "managed_document_revisions",
                columns: new[] { "TargetReleaseId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_build_selections_DocumentId",
                table: "managed_document_build_selections",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_build_selections_ProjectId",
                table: "managed_document_build_selections",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_build_selections_ReleaseId_DocumentId",
                table: "managed_document_build_selections",
                columns: new[] { "ReleaseId", "DocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_build_selections_RevisionId",
                table: "managed_document_build_selections",
                column: "RevisionId");

            migrationBuilder.AddForeignKey(
                name: "FK_managed_document_revisions_software_releases_TargetReleaseId",
                table: "managed_document_revisions",
                column: "TargetReleaseId",
                principalTable: "software_releases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
