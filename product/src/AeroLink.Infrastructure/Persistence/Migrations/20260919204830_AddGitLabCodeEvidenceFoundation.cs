using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGitLabCodeEvidenceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_release_campaigns_ProjectId_ReleaseId_Id",
                table: "release_campaigns",
                columns: new[] { "ProjectId", "ReleaseId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_project_repository_configurations_ProjectId_Id",
                table: "project_repository_configurations",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateTable(
                name: "code_evidence_disposition_sets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Disposition = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NoCodeChangeRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SourceSelectionEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededLegacyRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_evidence_disposition_sets", x => x.Id);
                    table.UniqueConstraint("AK_code_evidence_disposition_sets_ProjectId_ReleaseId_Requirem~", x => new { x.ProjectId, x.ReleaseId, x.RequirementArtifactId, x.RequirementRevisionId, x.Id });
                    table.ForeignKey(
                        name: "FK_code_evidence_disposition_sets_software_releases_ProjectId_~",
                        columns: x => new { x.ProjectId, x.ReleaseId },
                        principalTable: "software_releases",
                        principalColumns: new[] { "ProjectId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_review_cycle_manifest_identities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseCampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalCycle = table.Column<int>(type: "integer", nullable: false),
                    Format = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FormatVersion = table.Column<int>(type: "integer", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceSelectionEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceReferenceIdsJson = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                    FrozenBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FrozenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_review_cycle_manifest_identities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_review_cycle_manifest_identities_release_campaigns_Pro~",
                        columns: x => new { x.ProjectId, x.ReleaseId, x.ReleaseCampaignId },
                        principalTable: "release_campaigns",
                        principalColumns: new[] { "ProjectId", "ReleaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gitlab_code_relationship_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RelationshipId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gitlab_code_relationship_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "gitlab_merge_request_relationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceBaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RemoteProjectId = table.Column<long>(type: "bigint", nullable: false),
                    MergeRequestIid = table.Column<int>(type: "integer", nullable: false),
                    MergeRequestId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceSelectionEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepositoryPathSnapshot = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MergeRequestUrlSnapshot = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    MergeRequestTitleSnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RelationshipKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TargetKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetOwnerIdentityId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetRevisionNumber = table.Column<int>(type: "integer", nullable: true),
                    TargetStableIdentity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetDisplaySnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Meaning = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveEdgeKey = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WithdrawnBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WithdrawalRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReAddedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReAddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gitlab_merge_request_relationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_gitlab_merge_request_relationships_software_releases_Projec~",
                        columns: x => new { x.ProjectId, x.ReleaseId },
                        principalTable: "software_releases",
                        principalColumns: new[] { "ProjectId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gitlab_source_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceBaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RemoteProjectId = table.Column<long>(type: "bigint", nullable: false),
                    PathWithNamespace = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FriendlyRef = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfigurationVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gitlab_source_snapshots", x => x.Id);
                    table.UniqueConstraint("AK_gitlab_source_snapshots_ProjectId_Id", x => new { x.ProjectId, x.Id });
                    table.UniqueConstraint("AK_gitlab_source_snapshots_ProjectId_Id_InstanceBaseUrl_Remote~", x => new { x.ProjectId, x.Id, x.InstanceBaseUrl, x.RemoteProjectId, x.CommitSha });
                    table.ForeignKey(
                        name: "FK_gitlab_source_snapshots_project_repository_configurations_P~",
                        columns: x => new { x.ProjectId, x.RepositoryConfigurationId },
                        principalTable: "project_repository_configurations",
                        principalColumns: new[] { "ProjectId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gitlab_source_snapshots_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_evidence_current_selectors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    SelectedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_evidence_current_selectors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_evidence_current_selectors_code_evidence_disposition_s~",
                        columns: x => new { x.ProjectId, x.ReleaseId, x.RequirementArtifactId, x.RequirementRevisionId, x.EvidenceSetId },
                        principalTable: "code_evidence_disposition_sets",
                        principalColumns: new[] { "ProjectId", "ReleaseId", "RequirementArtifactId", "RequirementRevisionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_evidence_invalidations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvalidatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    InvalidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_evidence_invalidations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_evidence_invalidations_code_evidence_disposition_sets_~",
                        columns: x => new { x.ProjectId, x.ReleaseId, x.RequirementArtifactId, x.RequirementRevisionId, x.EvidenceSetId },
                        principalTable: "code_evidence_disposition_sets",
                        principalColumns: new[] { "ProjectId", "ReleaseId", "RequirementArtifactId", "RequirementRevisionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_evidence_contributions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContributionKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RelationshipId = table.Column<Guid>(type: "uuid", nullable: true),
                    InstanceBaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RemoteProjectId = table.Column<long>(type: "bigint", nullable: false),
                    RepositoryPathSnapshot = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MergeRequestIid = table.Column<int>(type: "integer", nullable: true),
                    MergeRequestId = table.Column<long>(type: "bigint", nullable: true),
                    MergeRequestUrlSnapshot = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MergeRequestTitleSnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StartLine = table.Column<int>(type: "integer", nullable: true),
                    EndLine = table.Column<int>(type: "integer", nullable: true),
                    TargetKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetOwnerIdentityId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetRevisionNumber = table.Column<int>(type: "integer", nullable: true),
                    TargetStableIdentity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetDisplaySnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_evidence_contributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_evidence_contributions_code_evidence_disposition_sets_~",
                        columns: x => new { x.ProjectId, x.ReleaseId, x.RequirementArtifactId, x.RequirementRevisionId, x.EvidenceSetId },
                        principalTable: "code_evidence_disposition_sets",
                        principalColumns: new[] { "ProjectId", "ReleaseId", "RequirementArtifactId", "RequirementRevisionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_evidence_contributions_gitlab_source_snapshots_Project~",
                        columns: x => new { x.ProjectId, x.SourceSnapshotId, x.InstanceBaseUrl, x.RemoteProjectId, x.CommitSha },
                        principalTable: "gitlab_source_snapshots",
                        principalColumns: new[] { "ProjectId", "Id", "InstanceBaseUrl", "RemoteProjectId", "CommitSha" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gitlab_source_selection_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedCurrentVersion = table.Column<long>(type: "bigint", nullable: false),
                    ResultingVersion = table.Column<long>(type: "bigint", nullable: false),
                    SelectedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gitlab_source_selection_events", x => x.Id);
                    table.UniqueConstraint("AK_gitlab_source_selection_events_ProjectId_ReleaseId_SourceS~1", x => new { x.ProjectId, x.ReleaseId, x.SourceSnapshotId, x.ResultingVersion, x.Id });
                    table.UniqueConstraint("AK_gitlab_source_selection_events_ProjectId_ReleaseId_SourceSn~", x => new { x.ProjectId, x.ReleaseId, x.SourceSnapshotId, x.Id });
                    table.ForeignKey(
                        name: "FK_gitlab_source_selection_events_gitlab_source_snapshots_Proj~",
                        columns: x => new { x.ProjectId, x.SourceSnapshotId },
                        principalTable: "gitlab_source_snapshots",
                        principalColumns: new[] { "ProjectId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gitlab_source_selection_events_software_releases_ProjectId_~",
                        columns: x => new { x.ProjectId, x.ReleaseId },
                        principalTable: "software_releases",
                        principalColumns: new[] { "ProjectId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gitlab_current_source_selections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectionEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gitlab_current_source_selections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_gitlab_current_source_selections_gitlab_source_selection_ev~",
                        columns: x => new { x.ProjectId, x.ReleaseId, x.SourceSnapshotId, x.Version, x.SelectionEventId },
                        principalTable: "gitlab_source_selection_events",
                        principalColumns: new[] { "ProjectId", "ReleaseId", "SourceSnapshotId", "ResultingVersion", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gitlab_file_relationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceBaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RemoteProjectId = table.Column<long>(type: "bigint", nullable: false),
                    SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSelectionEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    StartLine = table.Column<int>(type: "integer", nullable: true),
                    EndLine = table.Column<int>(type: "integer", nullable: true),
                    MergeRequestIid = table.Column<int>(type: "integer", nullable: true),
                    RelationshipKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TargetKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetOwnerIdentityId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetRevisionNumber = table.Column<int>(type: "integer", nullable: true),
                    TargetStableIdentity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetDisplaySnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Meaning = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveEdgeKey = table.Column<string>(type: "character varying(1600)", maxLength: 1600, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WithdrawnBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WithdrawalRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReAddedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReAddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gitlab_file_relationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_gitlab_file_relationships_gitlab_source_selection_events_Pr~",
                        columns: x => new { x.ProjectId, x.ReleaseId, x.SourceSnapshotId, x.SourceSelectionEventId },
                        principalTable: "gitlab_source_selection_events",
                        principalColumns: new[] { "ProjectId", "ReleaseId", "SourceSnapshotId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gitlab_file_relationships_gitlab_source_snapshots_ProjectId~",
                        columns: x => new { x.ProjectId, x.SourceSnapshotId, x.InstanceBaseUrl, x.RemoteProjectId, x.CommitSha },
                        principalTable: "gitlab_source_snapshots",
                        principalColumns: new[] { "ProjectId", "Id", "InstanceBaseUrl", "RemoteProjectId", "CommitSha" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gitlab_file_relationships_software_releases_ProjectId_Relea~",
                        columns: x => new { x.ProjectId, x.ReleaseId },
                        principalTable: "software_releases",
                        principalColumns: new[] { "ProjectId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_software_releases_ProjectId_Id",
                table: "software_releases",
                columns: new[] { "ProjectId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_campaigns_ProjectId_ReleaseId_Id",
                table: "release_campaigns",
                columns: new[] { "ProjectId", "ReleaseId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_repository_configurations_ProjectId_Id",
                table: "project_repository_configurations",
                columns: new[] { "ProjectId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_evidence_contributions_ProjectId_ReleaseId_Requiremen~1",
                table: "code_evidence_contributions",
                columns: new[] { "ProjectId", "ReleaseId", "RequirementRevisionId", "SourceSnapshotId", "EvidenceSetId" });

            migrationBuilder.CreateIndex(
                name: "IX_code_evidence_contributions_ProjectId_ReleaseId_Requirement~",
                table: "code_evidence_contributions",
                columns: new[] { "ProjectId", "ReleaseId", "RequirementArtifactId", "RequirementRevisionId", "EvidenceSetId" });

            migrationBuilder.CreateIndex(
                name: "IX_code_evidence_contributions_ProjectId_SourceSnapshotId_Inst~",
                table: "code_evidence_contributions",
                columns: new[] { "ProjectId", "SourceSnapshotId", "InstanceBaseUrl", "RemoteProjectId", "CommitSha" });

            migrationBuilder.CreateIndex(
                name: "IX_code_evidence_current_selectors_ProjectId_ReleaseId_Requir~1",
                table: "code_evidence_current_selectors",
                columns: new[] { "ProjectId", "ReleaseId", "RequirementArtifactId", "RequirementRevisionId", "EvidenceSetId" });

            migrationBuilder.CreateIndex(
                name: "IX_code_evidence_current_selectors_ProjectId_ReleaseId_Require~",
                table: "code_evidence_current_selectors",
                columns: new[] { "ProjectId", "ReleaseId", "RequirementRevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_evidence_disposition_sets_ProjectId_ReleaseId_Require~1",
                table: "code_evidence_disposition_sets",
                columns: new[] { "ProjectId", "ReleaseId", "RequirementArtifactId", "RequirementRevisionId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_evidence_disposition_sets_ProjectId_ReleaseId_Requirem~",
                table: "code_evidence_disposition_sets",
                columns: new[] { "ProjectId", "ReleaseId", "RequirementRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_code_evidence_invalidations_ProjectId_ReleaseId_Requirement~",
                table: "code_evidence_invalidations",
                columns: new[] { "ProjectId", "ReleaseId", "RequirementArtifactId", "RequirementRevisionId", "EvidenceSetId", "InvalidatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_code_review_cycle_manifest_identities_ProjectId_ReleaseCamp~",
                table: "code_review_cycle_manifest_identities",
                columns: new[] { "ProjectId", "ReleaseCampaignId", "ApprovalCycle" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_review_cycle_manifest_identities_ProjectId_ReleaseId_R~",
                table: "code_review_cycle_manifest_identities",
                columns: new[] { "ProjectId", "ReleaseId", "ReleaseCampaignId" });

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_code_relationship_events_RelationshipKind_Relationsh~",
                table: "gitlab_code_relationship_events",
                columns: new[] { "RelationshipKind", "RelationshipId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_current_source_selections_ProjectId_ReleaseId",
                table: "gitlab_current_source_selections",
                columns: new[] { "ProjectId", "ReleaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_current_source_selections_ProjectId_ReleaseId_Source~",
                table: "gitlab_current_source_selections",
                columns: new[] { "ProjectId", "ReleaseId", "SourceSnapshotId", "Version", "SelectionEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_file_relationships_ActiveEdgeKey",
                table: "gitlab_file_relationships",
                column: "ActiveEdgeKey",
                unique: true,
                filter: "\"ActiveEdgeKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_file_relationships_ProjectId_ReleaseId_IsActive",
                table: "gitlab_file_relationships",
                columns: new[] { "ProjectId", "ReleaseId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_file_relationships_ProjectId_ReleaseId_SourceSnapsho~",
                table: "gitlab_file_relationships",
                columns: new[] { "ProjectId", "ReleaseId", "SourceSnapshotId", "SourceSelectionEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_file_relationships_ProjectId_SourceSnapshotId_Instan~",
                table: "gitlab_file_relationships",
                columns: new[] { "ProjectId", "SourceSnapshotId", "InstanceBaseUrl", "RemoteProjectId", "CommitSha" });

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_merge_request_relationships_ActiveEdgeKey",
                table: "gitlab_merge_request_relationships",
                column: "ActiveEdgeKey",
                unique: true,
                filter: "\"ActiveEdgeKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_merge_request_relationships_ProjectId_ReleaseId_IsAc~",
                table: "gitlab_merge_request_relationships",
                columns: new[] { "ProjectId", "ReleaseId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_source_selection_events_ProjectId_ReleaseId_Resultin~",
                table: "gitlab_source_selection_events",
                columns: new[] { "ProjectId", "ReleaseId", "ResultingVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_source_selection_events_ProjectId_ReleaseId_SourceS~1",
                table: "gitlab_source_selection_events",
                columns: new[] { "ProjectId", "ReleaseId", "SourceSnapshotId", "ResultingVersion", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_source_selection_events_ProjectId_ReleaseId_SourceSn~",
                table: "gitlab_source_selection_events",
                columns: new[] { "ProjectId", "ReleaseId", "SourceSnapshotId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_source_selection_events_ProjectId_SourceSnapshotId",
                table: "gitlab_source_selection_events",
                columns: new[] { "ProjectId", "SourceSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_source_snapshots_ProjectId_Id",
                table: "gitlab_source_snapshots",
                columns: new[] { "ProjectId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_source_snapshots_ProjectId_Id_InstanceBaseUrl_Remot~1",
                table: "gitlab_source_snapshots",
                columns: new[] { "ProjectId", "Id", "InstanceBaseUrl", "RemoteProjectId", "CommitSha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_source_snapshots_ProjectId_Id_InstanceBaseUrl_Remote~",
                table: "gitlab_source_snapshots",
                columns: new[] { "ProjectId", "Id", "InstanceBaseUrl", "RemoteProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gitlab_source_snapshots_ProjectId_RepositoryConfigurationId",
                table: "gitlab_source_snapshots",
                columns: new[] { "ProjectId", "RepositoryConfigurationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_evidence_contributions");

            migrationBuilder.DropTable(
                name: "code_evidence_current_selectors");

            migrationBuilder.DropTable(
                name: "code_evidence_invalidations");

            migrationBuilder.DropTable(
                name: "code_review_cycle_manifest_identities");

            migrationBuilder.DropTable(
                name: "gitlab_code_relationship_events");

            migrationBuilder.DropTable(
                name: "gitlab_current_source_selections");

            migrationBuilder.DropTable(
                name: "gitlab_file_relationships");

            migrationBuilder.DropTable(
                name: "gitlab_merge_request_relationships");

            migrationBuilder.DropTable(
                name: "code_evidence_disposition_sets");

            migrationBuilder.DropTable(
                name: "gitlab_source_selection_events");

            migrationBuilder.DropTable(
                name: "gitlab_source_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_software_releases_ProjectId_Id",
                table: "software_releases");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_release_campaigns_ProjectId_ReleaseId_Id",
                table: "release_campaigns");

            migrationBuilder.DropIndex(
                name: "IX_release_campaigns_ProjectId_ReleaseId_Id",
                table: "release_campaigns");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_project_repository_configurations_ProjectId_Id",
                table: "project_repository_configurations");

            migrationBuilder.DropIndex(
                name: "IX_project_repository_configurations_ProjectId_Id",
                table: "project_repository_configurations");
        }
    }
}
