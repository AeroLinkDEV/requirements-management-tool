using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedDocumentationCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "managed_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Acronym = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_documents_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_document_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_document_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_document_events_managed_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "managed_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "managed_document_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChangeSummary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CurrentWorkingAttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseCandidateDocxAttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseCandidatePdfAttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleasedDocxAttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleasedPdfAttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReleaseManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReturnReason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_document_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_document_revisions_managed_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "managed_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_revisions_software_releases_TargetReleaseId",
                        column: x => x.TargetReleaseId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_connector_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EditSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LaunchTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccessTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_connector_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_connector_grants_artifact_edit_sessions_EditSessio~",
                        column: x => x.EditSessionId,
                        principalTable: "artifact_edit_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_connector_grants_managed_document_revisions_Revisi~",
                        column: x => x.RevisionId,
                        principalTable: "managed_document_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_connector_grants_managed_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "managed_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_document_build_selections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "managed_document_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Relationship = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_document_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_document_links_managed_document_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "managed_document_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "managed_document_review_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cycle = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ApproverId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApproverName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StageName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_document_review_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_document_review_steps_managed_document_revisions_Re~",
                        column: x => x.RevisionId,
                        principalTable: "managed_document_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_connector_grants_AccessTokenHash",
                table: "document_connector_grants",
                column: "AccessTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_connector_grants_DocumentId",
                table: "document_connector_grants",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_document_connector_grants_EditSessionId",
                table: "document_connector_grants",
                column: "EditSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_document_connector_grants_LaunchTokenHash",
                table: "document_connector_grants",
                column: "LaunchTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_connector_grants_RevisionId",
                table: "document_connector_grants",
                column: "RevisionId");

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

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_events_DocumentId_OccurredAt",
                table: "managed_document_events",
                columns: new[] { "DocumentId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_links_ArtifactType_ArtifactId",
                table: "managed_document_links",
                columns: new[] { "ArtifactType", "ArtifactId" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_links_RevisionId_ArtifactType_ArtifactId_R~",
                table: "managed_document_links",
                columns: new[] { "RevisionId", "ArtifactType", "ArtifactId", "Relationship" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_review_steps_RevisionId_Cycle_Position",
                table: "managed_document_review_steps",
                columns: new[] { "RevisionId", "Cycle", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_revisions_DocumentId_Revision",
                table: "managed_document_revisions",
                columns: new[] { "DocumentId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_revisions_TargetReleaseId_State",
                table: "managed_document_revisions",
                columns: new[] { "TargetReleaseId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_documents_ProjectId_Acronym",
                table: "managed_documents",
                columns: new[] { "ProjectId", "Acronym" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_documents_ProjectId_DocumentNumber",
                table: "managed_documents",
                columns: new[] { "ProjectId", "DocumentNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_connector_grants");

            migrationBuilder.DropTable(
                name: "managed_document_build_selections");

            migrationBuilder.DropTable(
                name: "managed_document_events");

            migrationBuilder.DropTable(
                name: "managed_document_links");

            migrationBuilder.DropTable(
                name: "managed_document_review_steps");

            migrationBuilder.DropTable(
                name: "managed_document_revisions");

            migrationBuilder.DropTable(
                name: "managed_documents");
        }
    }
}
