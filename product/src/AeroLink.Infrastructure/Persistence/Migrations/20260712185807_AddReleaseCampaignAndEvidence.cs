using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseCampaignAndEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleasedAt",
                table: "software_releases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "evidence_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UploadedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_evidence_records_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    SoftwareBuildId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_campaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_release_campaigns_candidate_baselines_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_campaigns_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_campaigns_software_builds_SoftwareBuildId",
                        column: x => x.SoftwareBuildId,
                        principalTable: "software_builds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_campaigns_software_releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "test_execution_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TestExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_execution_evidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_execution_evidence_evidence_records_EvidenceId",
                        column: x => x.EvidenceId,
                        principalTable: "evidence_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_execution_evidence_test_executions_TestExecutionId",
                        column: x => x.TestExecutionId,
                        principalTable: "test_executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "change_impact_dispositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScrId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ArtifactReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DispositionedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DispositionedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_impact_dispositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_change_impact_dispositions_release_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "release_campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_change_impact_dispositions_system_change_requests_ScrId",
                        column: x => x.ScrId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ApproverId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApproverName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_approvals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_release_approvals_release_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "release_campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_campaign_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_campaign_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_release_campaign_events_release_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "release_campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_change_impact_dispositions_CampaignId_ScrId_Kind_ArtifactRe~",
                table: "change_impact_dispositions",
                columns: new[] { "CampaignId", "ScrId", "Kind", "ArtifactReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_change_impact_dispositions_ScrId",
                table: "change_impact_dispositions",
                column: "ScrId");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_records_ProjectId_Sha256",
                table: "evidence_records",
                columns: new[] { "ProjectId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_release_approvals_CampaignId_Position",
                table: "release_approvals",
                columns: new[] { "CampaignId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_campaign_events_CampaignId_OccurredAt",
                table: "release_campaign_events",
                columns: new[] { "CampaignId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_release_campaigns_BaselineId",
                table: "release_campaigns",
                column: "BaselineId");

            migrationBuilder.CreateIndex(
                name: "IX_release_campaigns_ProjectId_ReleaseId",
                table: "release_campaigns",
                columns: new[] { "ProjectId", "ReleaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_campaigns_ReleaseId",
                table: "release_campaigns",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_release_campaigns_SoftwareBuildId",
                table: "release_campaigns",
                column: "SoftwareBuildId");

            migrationBuilder.CreateIndex(
                name: "IX_test_execution_evidence_EvidenceId",
                table: "test_execution_evidence",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_test_execution_evidence_TestExecutionId_EvidenceId",
                table: "test_execution_evidence",
                columns: new[] { "TestExecutionId", "EvidenceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_impact_dispositions");

            migrationBuilder.DropTable(
                name: "release_approvals");

            migrationBuilder.DropTable(
                name: "release_campaign_events");

            migrationBuilder.DropTable(
                name: "test_execution_evidence");

            migrationBuilder.DropTable(
                name: "release_campaigns");

            migrationBuilder.DropTable(
                name: "evidence_records");

            migrationBuilder.DropColumn(
                name: "ReleasedAt",
                table: "software_releases");
        }
    }
}
