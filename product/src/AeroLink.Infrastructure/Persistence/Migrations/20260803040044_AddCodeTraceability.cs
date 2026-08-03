using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeTraceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "code_traceability_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Disposition = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RepositoryPath = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MergeRequestReference = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    MergeRequestTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MergeRequestUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    MergeCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MergedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NoCodeChangeRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    IsDemonstration = table.Column<bool>(type: "boolean", nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_traceability_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_traceability_records_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_traceability_records_requirement_revisions_Requirement~",
                        column: x => x.RequirementRevisionId,
                        principalTable: "requirement_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_traceability_records_requirements_RequirementArtifactId",
                        column: x => x.RequirementArtifactId,
                        principalTable: "requirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_traceability_records_software_releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_traceability_records_ProjectId_ReleaseId",
                table: "code_traceability_records",
                columns: new[] { "ProjectId", "ReleaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_code_traceability_records_ReleaseId_RequirementRevisionId",
                table: "code_traceability_records",
                columns: new[] { "ReleaseId", "RequirementRevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_traceability_records_RequirementArtifactId",
                table: "code_traceability_records",
                column: "RequirementArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_code_traceability_records_RequirementRevisionId",
                table: "code_traceability_records",
                column: "RequirementRevisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_traceability_records");
        }
    }
}
