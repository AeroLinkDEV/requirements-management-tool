using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthoritativeRequirementBaselines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequirementsHash",
                table: "candidate_baselines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RequirementsMaterializedAt",
                table: "candidate_baselines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "requirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Level = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_requirements_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "requirement_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Statement = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    VerificationMethod = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceScrId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveBaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirement_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_requirement_revisions_candidate_baselines_EffectiveBaseline~",
                        column: x => x.EffectiveBaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_requirement_revisions_requirements_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "requirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_requirement_revisions_system_change_requests_SourceScrId",
                        column: x => x.SourceScrId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "baseline_requirement_selections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baseline_requirement_selections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_baseline_requirement_selections_candidate_baselines_Baselin~",
                        column: x => x.BaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_baseline_requirement_selections_requirement_revisions_Revis~",
                        column: x => x.RevisionId,
                        principalTable: "requirement_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_baseline_requirement_selections_requirements_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "requirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_baseline_requirement_selections_ArtifactId",
                table: "baseline_requirement_selections",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_baseline_requirement_selections_BaselineId_ArtifactId",
                table: "baseline_requirement_selections",
                columns: new[] { "BaselineId", "ArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_baseline_requirement_selections_BaselineId_RevisionId",
                table: "baseline_requirement_selections",
                columns: new[] { "BaselineId", "RevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_baseline_requirement_selections_RevisionId",
                table: "baseline_requirement_selections",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_requirement_revisions_ArtifactId_Revision",
                table: "requirement_revisions",
                columns: new[] { "ArtifactId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requirement_revisions_EffectiveBaselineId",
                table: "requirement_revisions",
                column: "EffectiveBaselineId");

            migrationBuilder.CreateIndex(
                name: "IX_requirement_revisions_SourceScrId",
                table: "requirement_revisions",
                column: "SourceScrId");

            migrationBuilder.CreateIndex(
                name: "IX_requirements_ProjectId_BaseNumber",
                table: "requirements",
                columns: new[] { "ProjectId", "BaseNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "baseline_requirement_selections");

            migrationBuilder.DropTable(
                name: "requirement_revisions");

            migrationBuilder.DropTable(
                name: "requirements");

            migrationBuilder.DropColumn(
                name: "RequirementsHash",
                table: "candidate_baselines");

            migrationBuilder.DropColumn(
                name: "RequirementsMaterializedAt",
                table: "candidate_baselines");
        }
    }
}
