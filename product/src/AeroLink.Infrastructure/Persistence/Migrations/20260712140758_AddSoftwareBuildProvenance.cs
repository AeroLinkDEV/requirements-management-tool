using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftwareBuildProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "software_builds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_software_builds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_software_builds_candidate_baselines_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_software_builds_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_software_builds_software_releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_software_builds_BaselineId",
                table: "software_builds",
                column: "BaselineId");

            migrationBuilder.CreateIndex(
                name: "IX_software_builds_ProjectId_BuildNumber",
                table: "software_builds",
                columns: new[] { "ProjectId", "BuildNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_software_builds_ProjectId_ReleaseId_RecordedAt",
                table: "software_builds",
                columns: new[] { "ProjectId", "ReleaseId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_software_builds_ReleaseId",
                table: "software_builds",
                column: "ReleaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "software_builds");
        }
    }
}
