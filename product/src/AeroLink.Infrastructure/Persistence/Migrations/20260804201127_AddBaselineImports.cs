using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBaselineImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "baseline_imports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SourceSystemVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SourceBaselineName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceBaselineDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExtractFileName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    ExtractSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExtractSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Carries = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ExtractedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExtractedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MappingJson = table.Column<string>(type: "text", nullable: false),
                    ReconciliationJson = table.Column<string>(type: "text", nullable: false),
                    AcceptedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baseline_imports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_baseline_imports_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_baseline_imports_software_releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "source_identities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SourceModule = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceObjectKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SourceIdentifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InImportedBaseline = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_identities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_source_identities_baseline_imports_BaselineImportId",
                        column: x => x.BaselineImportId,
                        principalTable: "baseline_imports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "source_history_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceBaselineName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Statement = table.Column<string>(type: "text", nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceChangeReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_history_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_source_history_entries_baseline_imports_BaselineImportId",
                        column: x => x.BaselineImportId,
                        principalTable: "baseline_imports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_source_history_entries_source_identities_SourceIdentityId",
                        column: x => x.SourceIdentityId,
                        principalTable: "source_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_identity_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_identity_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_source_identity_links_baseline_imports_BaselineImportId",
                        column: x => x.BaselineImportId,
                        principalTable: "baseline_imports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_source_identity_links_requirement_revisions_RequirementRevi~",
                        column: x => x.RequirementRevisionId,
                        principalTable: "requirement_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_source_identity_links_source_identities_SourceIdentityId",
                        column: x => x.SourceIdentityId,
                        principalTable: "source_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_baseline_imports_ProjectId_State",
                table: "baseline_imports",
                columns: new[] { "ProjectId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_baseline_imports_ReleaseId",
                table: "baseline_imports",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_source_history_entries_BaselineImportId",
                table: "source_history_entries",
                column: "BaselineImportId");

            migrationBuilder.CreateIndex(
                name: "IX_source_history_entries_SourceIdentityId_SourceBaselineName",
                table: "source_history_entries",
                columns: new[] { "SourceIdentityId", "SourceBaselineName" });

            migrationBuilder.CreateIndex(
                name: "IX_source_identities_BaselineImportId",
                table: "source_identities",
                column: "BaselineImportId");

            migrationBuilder.CreateIndex(
                name: "IX_source_identities_ProjectId_SourceIdentifier",
                table: "source_identities",
                columns: new[] { "ProjectId", "SourceIdentifier" });

            migrationBuilder.CreateIndex(
                name: "IX_source_identities_ProjectId_SourceSystem_SourceModule_Sourc~",
                table: "source_identities",
                columns: new[] { "ProjectId", "SourceSystem", "SourceModule", "SourceObjectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_identity_links_BaselineImportId",
                table: "source_identity_links",
                column: "BaselineImportId");

            migrationBuilder.CreateIndex(
                name: "IX_source_identity_links_RequirementRevisionId_SourceIdentityId",
                table: "source_identity_links",
                columns: new[] { "RequirementRevisionId", "SourceIdentityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_identity_links_SourceIdentityId",
                table: "source_identity_links",
                column: "SourceIdentityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_history_entries");

            migrationBuilder.DropTable(
                name: "source_identity_links");

            migrationBuilder.DropTable(
                name: "source_identities");

            migrationBuilder.DropTable(
                name: "baseline_imports");
        }
    }
}
