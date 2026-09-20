using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleasedSyntheticSourceSupplement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_candidate_baselines_ProjectId_ReleaseId_Id",
                table: "candidate_baselines",
                columns: new[] { "ProjectId", "ReleaseId", "Id" });

            migrationBuilder.CreateTable(
                name: "released_synthetic_source_supplements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseCampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigurationVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceBaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RemoteProjectId = table.Column<long>(type: "bigint", nullable: false),
                    RepositoryPath = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ReferenceKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestVersion = table.Column<int>(type: "integer", nullable: false),
                    AuthorityScopeVersion = table.Column<int>(type: "integer", nullable: false),
                    AuthorityScopeDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ManifestDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AuthorizationReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_released_synthetic_source_supplements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_released_synthetic_source_supplements_candidate_baselines_P~",
                        columns: x => new { x.ProjectId, x.ReleaseId, x.BaselineId },
                        principalTable: "candidate_baselines",
                        principalColumns: new[] { "ProjectId", "ReleaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_released_synthetic_source_supplements_gitlab_source_snapsho~",
                        columns: x => new { x.ProjectId, x.SourceSnapshotId },
                        principalTable: "gitlab_source_snapshots",
                        principalColumns: new[] { "ProjectId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_released_synthetic_source_supplements_project_repository_co~",
                        columns: x => new { x.ProjectId, x.RepositoryConfigurationId },
                        principalTable: "project_repository_configurations",
                        principalColumns: new[] { "ProjectId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_released_synthetic_source_supplements_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_released_synthetic_source_supplements_release_campaigns_Pro~",
                        columns: x => new { x.ProjectId, x.ReleaseId, x.ReleaseCampaignId },
                        principalTable: "release_campaigns",
                        principalColumns: new[] { "ProjectId", "ReleaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_released_synthetic_source_supplements_software_releases_Pro~",
                        columns: x => new { x.ProjectId, x.ReleaseId },
                        principalTable: "software_releases",
                        principalColumns: new[] { "ProjectId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_released_synthetic_source_supplements_ManifestDigest",
                table: "released_synthetic_source_supplements",
                column: "ManifestDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_released_synthetic_source_supplements_ProjectId_OperationId",
                table: "released_synthetic_source_supplements",
                columns: new[] { "ProjectId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_released_synthetic_source_supplements_ProjectId_ReleaseId",
                table: "released_synthetic_source_supplements",
                columns: new[] { "ProjectId", "ReleaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_released_synthetic_source_supplements_ProjectId_ReleaseId_B~",
                table: "released_synthetic_source_supplements",
                columns: new[] { "ProjectId", "ReleaseId", "BaselineId" });

            migrationBuilder.CreateIndex(
                name: "IX_released_synthetic_source_supplements_ProjectId_ReleaseId_R~",
                table: "released_synthetic_source_supplements",
                columns: new[] { "ProjectId", "ReleaseId", "ReleaseCampaignId" });

            migrationBuilder.CreateIndex(
                name: "IX_released_synthetic_source_supplements_ProjectId_RepositoryC~",
                table: "released_synthetic_source_supplements",
                columns: new[] { "ProjectId", "RepositoryConfigurationId" });

            migrationBuilder.CreateIndex(
                name: "IX_released_synthetic_source_supplements_ProjectId_SourceSnaps~",
                table: "released_synthetic_source_supplements",
                columns: new[] { "ProjectId", "SourceSnapshotId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "released_synthetic_source_supplements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_candidate_baselines_ProjectId_ReleaseId_Id",
                table: "candidate_baselines");
        }
    }
}
