using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectAssurancePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssurancePolicyVersionId",
                table: "release_campaigns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_assurance_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DeclaredLevel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AuthorityPolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    SelectionsSnapshot = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_assurance_policies", x => x.Id);
                    table.CheckConstraint("CK_project_assurance_policy_version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_project_assurance_policies_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assurance_policy_deviations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    Lever = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecommendedValue = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RecommendationBasis = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    BasisKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SelectedValue = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DeviationClass = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AirworthinessDesignated = table.Column<bool>(type: "boolean", nullable: false),
                    ReleaseEffect = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ProposedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProposedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProposedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApprovalAuthority = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ApprovalAuthoritySource = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AuthorityPolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SupersededReason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    RecordHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assurance_policy_deviations", x => x.Id);
                    table.CheckConstraint("CK_assurance_deviation_distinct_parties", "\"ProposedByAccountId\" <> \"ApprovedByAccountId\"");
                    table.ForeignKey(
                        name: "FK_assurance_policy_deviations_project_assurance_policies_Poli~",
                        column: x => x.PolicyVersionId,
                        principalTable: "project_assurance_policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assurance_policy_deviations_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assurance_policy_deviations_PolicyVersionId",
                table: "assurance_policy_deviations",
                column: "PolicyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_assurance_policy_deviations_ProjectId_Lever_EffectiveFrom",
                table: "assurance_policy_deviations",
                columns: new[] { "ProjectId", "Lever", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_project_assurance_policies_effective",
                table: "project_assurance_policies",
                column: "ProjectId",
                unique: true,
                filter: "\"SupersededAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_project_assurance_policies_ProjectId_Version",
                table: "project_assurance_policies",
                columns: new[] { "ProjectId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assurance_policy_deviations");

            migrationBuilder.DropTable(
                name: "project_assurance_policies");

            migrationBuilder.DropColumn(
                name: "AssurancePolicyVersionId",
                table: "release_campaigns");
        }
    }
}
