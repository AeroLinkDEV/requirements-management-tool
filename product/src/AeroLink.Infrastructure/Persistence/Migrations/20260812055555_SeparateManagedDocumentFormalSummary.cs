using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeparateManagedDocumentFormalSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FormalSummaryHash",
                table: "managed_document_revisions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FormalSummaryProvenance",
                table: "managed_document_revisions",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "FormalSummaryVersion",
                table: "managed_document_revisions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "SubmittedFormalSummaryHash",
                table: "managed_document_revisions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SubmittedFormalSummaryVersion",
                table: "managed_document_revisions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "managed_document_check_ins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkingAttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkingVersion = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    BaseAttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    BaseSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResultSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupersededAttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectorSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReturnResolutionNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_document_check_ins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_document_check_ins_artifact_edit_sessions_Connector~",
                        column: x => x.ConnectorSessionId,
                        principalTable: "artifact_edit_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_check_ins_controlled_attachments_BaseAttac~",
                        column: x => x.BaseAttachmentId,
                        principalTable: "controlled_attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_check_ins_controlled_attachments_Supersede~",
                        column: x => x.SupersededAttachmentId,
                        principalTable: "controlled_attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_check_ins_controlled_attachments_WorkingAt~",
                        column: x => x.WorkingAttachmentId,
                        principalTable: "controlled_attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_check_ins_managed_document_revisions_Revis~",
                        column: x => x.RevisionId,
                        principalTable: "managed_document_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // The former ChangeSummary field was overwritten by every check-in, so its
            // retained value cannot truthfully be promoted to an authoritative original
            // formal summary. Preserve that value and hash it, but mark its provenance.
            migrationBuilder.Sql(
                """
                UPDATE managed_document_revisions
                SET "FormalSummaryHash" = encode(sha256(convert_to("ChangeSummary", 'UTF8')), 'hex'),
                    "FormalSummaryVersion" = 1,
                    "FormalSummaryProvenance" = 'LegacyAmbiguousLatestValue';
                """);

            // Recover every historical working attachment as immutable check-in evidence.
            // Connector session identity and the original operation token were never
            // retained by the legacy schema, so those fields remain explicitly unknown.
            migrationBuilder.Sql(
                """
                INSERT INTO managed_document_check_ins
                    ("Id", "RevisionId", "WorkingAttachmentId", "WorkingVersion", "ActorId", "Comment",
                     "BaseAttachmentId", "BaseSha256", "ResultSha256", "SupersededAttachmentId",
                     "ConnectorSessionId", "OperationId", "OccurredAt", "ReturnResolutionNote")
                SELECT attachment."Id", attachment."RevisionId", attachment."Id", attachment."Version",
                       attachment."UploadedBy",
                       CASE WHEN btrim(attachment."Description") = ''
                            THEN 'Legacy check-in comment unavailable.' ELSE attachment."Description" END,
                       attachment."SupersedesId", base."Sha256", attachment."Sha256", attachment."SupersedesId",
                       NULL, 'legacy-attachment:' || attachment."Id"::text, attachment."UploadedAt", NULL
                FROM controlled_attachments attachment
                LEFT JOIN controlled_attachments base ON base."Id" = attachment."SupersedesId"
                WHERE attachment."ArtifactType" = 'ManagedDocument'
                  AND attachment."RevisionId" IS NOT NULL
                  AND attachment."Label" = 'Working Word document';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_check_ins_BaseAttachmentId",
                table: "managed_document_check_ins",
                column: "BaseAttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_check_ins_ConnectorSessionId",
                table: "managed_document_check_ins",
                column: "ConnectorSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_check_ins_RevisionId_WorkingVersion",
                table: "managed_document_check_ins",
                columns: new[] { "RevisionId", "WorkingVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_check_ins_SupersededAttachmentId",
                table: "managed_document_check_ins",
                column: "SupersededAttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_check_ins_WorkingAttachmentId",
                table: "managed_document_check_ins",
                column: "WorkingAttachmentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "managed_document_check_ins");

            migrationBuilder.DropColumn(
                name: "FormalSummaryHash",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "FormalSummaryProvenance",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "FormalSummaryVersion",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "SubmittedFormalSummaryHash",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "SubmittedFormalSummaryVersion",
                table: "managed_document_revisions");
        }
    }
}
