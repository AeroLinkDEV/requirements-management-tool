using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSetupAndRepositoryVerificationFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanonicalIdentity",
                table: "software_releases",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_repository_configurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConfiguredBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ConfiguredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastVerifiedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RemoteProjectId = table.Column<long>(type: "bigint", nullable: true),
                    RemotePathWithNamespace = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LastVerificationFailureAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastVerificationFailureBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_repository_configurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_repository_configurations_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_setup_drafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorUserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InternalProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    InternalProgramName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InternalProgramCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitialReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CurrentStep = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProjectName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SoftwareProduct = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartKind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SourceBaselineId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceImportId = table.Column<Guid>(type: "uuid", nullable: true),
                    InitialReleaseVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    InitialReleaseCanonicalIdentity = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SelectedCategoriesJson = table.Column<string>(type: "text", nullable: false),
                    LadderJson = table.Column<string>(type: "text", nullable: false),
                    ReviewRulesJson = table.Column<string>(type: "text", nullable: false),
                    RepositoryJson = table.Column<string>(type: "text", nullable: false),
                    MappingJson = table.Column<string>(type: "text", nullable: false),
                    ReviewRulesAccepted = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewRulesAcceptanceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSavedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinalizationStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinalizationOperationKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FinalizationResultJson = table.Column<string>(type: "text", nullable: true),
                    CompletedProgramId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedReleaseId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_setup_drafts", x => x.Id);
                    table.CheckConstraint("CK_project_setup_draft_version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_project_setup_drafts_user_accounts_CreatorUserId",
                        column: x => x.CreatorUserId,
                        principalTable: "user_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_software_releases_ProjectId_CanonicalIdentity",
                table: "software_releases",
                columns: new[] { "ProjectId", "CanonicalIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_repository_configurations_ProjectId",
                table: "project_repository_configurations",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_drafts_CreatorUserId_State",
                table: "project_setup_drafts",
                columns: new[] { "CreatorUserId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_drafts_InitialReleaseId",
                table: "project_setup_drafts",
                column: "InitialReleaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_drafts_InternalProgramCode",
                table: "project_setup_drafts",
                column: "InternalProgramCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_drafts_InternalProgramId",
                table: "project_setup_drafts",
                column: "InternalProgramId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_drafts_ProjectId",
                table: "project_setup_drafts",
                column: "ProjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_repository_configurations");

            migrationBuilder.DropTable(
                name: "project_setup_drafts");

            migrationBuilder.DropIndex(
                name: "IX_software_releases_ProjectId_CanonicalIdentity",
                table: "software_releases");

            migrationBuilder.DropColumn(
                name: "CanonicalIdentity",
                table: "software_releases");
        }
    }
}
