using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFmsShowcaseLifecycleModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Level",
                table: "test_procedures",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "HighLevel");

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "system_change_requests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "System");

            migrationBuilder.CreateTable(
                name: "controlled_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactCount = table.Column<int>(type: "integer", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_controlled_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_controlled_documents_candidate_baselines_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_controlled_documents_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_controlled_documents_software_releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "requirement_trace_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirement_trace_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_requirement_trace_links_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_requirement_trace_links_requirement_revisions_SourceRevisio~",
                        column: x => x.SourceRevisionId,
                        principalTable: "requirement_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_requirement_trace_links_requirement_revisions_TargetRevisio~",
                        column: x => x.TargetRevisionId,
                        principalTable: "requirement_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_controlled_documents_BaselineId_Type",
                table: "controlled_documents",
                columns: new[] { "BaselineId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_controlled_documents_ProjectId_DocumentNumber_Revision",
                table: "controlled_documents",
                columns: new[] { "ProjectId", "DocumentNumber", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_controlled_documents_ReleaseId",
                table: "controlled_documents",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_requirement_trace_links_ProjectId",
                table: "requirement_trace_links",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_requirement_trace_links_SourceRevisionId_TargetRevisionId_T~",
                table: "requirement_trace_links",
                columns: new[] { "SourceRevisionId", "TargetRevisionId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requirement_trace_links_TargetRevisionId",
                table: "requirement_trace_links",
                column: "TargetRevisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "controlled_documents");

            migrationBuilder.DropTable(
                name: "requirement_trace_links");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "test_procedures");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "system_change_requests");
        }
    }
}
