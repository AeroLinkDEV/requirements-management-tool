using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildTestSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "build_test_sets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Discipline = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_test_sets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_build_test_sets_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_build_test_sets_software_releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "build_test_set_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildTestSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcedureRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AddedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_test_set_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_build_test_set_entries_build_test_sets_BuildTestSetId",
                        column: x => x.BuildTestSetId,
                        principalTable: "build_test_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_build_test_set_entries_test_procedure_revisions_ProcedureRe~",
                        column: x => x.ProcedureRevisionId,
                        principalTable: "test_procedure_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_build_test_set_entries_BuildTestSetId_ProcedureRevisionId",
                table: "build_test_set_entries",
                columns: new[] { "BuildTestSetId", "ProcedureRevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_build_test_set_entries_ProcedureRevisionId",
                table: "build_test_set_entries",
                column: "ProcedureRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_build_test_sets_ProjectId",
                table: "build_test_sets",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_build_test_sets_ReleaseId_Discipline",
                table: "build_test_sets",
                columns: new[] { "ReleaseId", "Discipline" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "build_test_set_entries");

            migrationBuilder.DropTable(
                name: "build_test_sets");
        }
    }
}
