using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEnterpriseHardeningControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempt",
                table: "enterprise_operation_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "enterprise_operation_jobs",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "enterprise_operation_jobs",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressPercent",
                table: "enterprise_operation_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "enterprise_operation_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "enterprise_operation_jobs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql("UPDATE enterprise_operation_jobs SET \"IdempotencyKey\" = 'legacy-' || \"Id\"::text, \"UpdatedAt\" = \"CreatedAt\"");

            migrationBuilder.CreateTable(
                name: "artifact_edit_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    BaseSnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DraftJson = table.Column<string>(type: "text", nullable: false),
                    UserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_edit_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "artifact_merge_conflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompetingSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseJson = table.Column<string>(type: "text", nullable: false),
                    LocalJson = table.Column<string>(type: "text", nullable: false),
                    RemoteJson = table.Column<string>(type: "text", nullable: false),
                    ResolutionJson = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_merge_conflicts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "controlled_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LogicalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SupersedesId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    UploadedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IntegrityVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_controlled_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_controlled_attachments_controlled_attachments_SupersedesId",
                        column: x => x.SupersedesId,
                        principalTable: "controlled_attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "enterprise_integrity_checkpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactCount = table.Column<int>(type: "integer", nullable: false),
                    RevisionCount = table.Column<int>(type: "integer", nullable: false),
                    AttachmentCount = table.Column<int>(type: "integer", nullable: false),
                    AttachmentBytes = table.Column<long>(type: "bigint", nullable: false),
                    FailedJobCount = table.Column<int>(type: "integer", nullable: false),
                    OpenConflictCount = table.Column<int>(type: "integer", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enterprise_integrity_checkpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_enterprise_operation_jobs_ProjectId_IdempotencyKey",
                table: "enterprise_operation_jobs",
                columns: new[] { "ProjectId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_artifact_edit_sessions_ProjectId_ArtifactType_ArtifactId_St~",
                table: "artifact_edit_sessions",
                columns: new[] { "ProjectId", "ArtifactType", "ArtifactId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_edit_sessions_UserName_UpdatedAt",
                table: "artifact_edit_sessions",
                columns: new[] { "UserName", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_merge_conflicts_ProjectId_ArtifactId_ResolvedAt",
                table: "artifact_merge_conflicts",
                columns: new[] { "ProjectId", "ArtifactId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_controlled_attachments_LogicalId_Version",
                table: "controlled_attachments",
                columns: new[] { "LogicalId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_controlled_attachments_ProjectId_ArtifactType_ArtifactId_St~",
                table: "controlled_attachments",
                columns: new[] { "ProjectId", "ArtifactType", "ArtifactId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_controlled_attachments_ProjectId_Sha256",
                table: "controlled_attachments",
                columns: new[] { "ProjectId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_controlled_attachments_SupersedesId",
                table: "controlled_attachments",
                column: "SupersedesId");

            migrationBuilder.CreateIndex(
                name: "IX_enterprise_integrity_checkpoints_ProjectId_CreatedAt",
                table: "enterprise_integrity_checkpoints",
                columns: new[] { "ProjectId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifact_edit_sessions");

            migrationBuilder.DropTable(
                name: "artifact_merge_conflicts");

            migrationBuilder.DropTable(
                name: "controlled_attachments");

            migrationBuilder.DropTable(
                name: "enterprise_integrity_checkpoints");

            migrationBuilder.DropIndex(
                name: "IX_enterprise_operation_jobs_ProjectId_IdempotencyKey",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "Attempt",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "ProgressPercent",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "enterprise_operation_jobs");
        }
    }
}
