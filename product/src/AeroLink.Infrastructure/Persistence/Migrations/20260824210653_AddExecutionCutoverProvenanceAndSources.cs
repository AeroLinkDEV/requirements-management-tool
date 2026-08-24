using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionCutoverProvenanceAndSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "baseline_execution_cutover_provenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    TotalMappings = table.Column<int>(type: "integer", nullable: false),
                    CanonicalAggregateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baseline_execution_cutover_provenance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_baseline_execution_cutover_provenance_candidate_baselines_B~",
                        column: x => x.BaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "test_procedure_migration_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceCaseRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeneratedProcedureArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeneratedProcedureRevisionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_procedure_migration_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_procedure_migration_sources_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_baseline_execution_cutover_provenance_BaselineId_Sequence",
                table: "baseline_execution_cutover_provenance",
                columns: new[] { "BaselineId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_baseline_execution_cutover_provenance_EventId",
                table: "baseline_execution_cutover_provenance",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_migration_sources_GeneratedProcedureRevision~",
                table: "test_procedure_migration_sources",
                column: "GeneratedProcedureRevisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_migration_sources_ProjectId",
                table: "test_procedure_migration_sources",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_migration_sources_SourceCaseRevisionId",
                table: "test_procedure_migration_sources",
                column: "SourceCaseRevisionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "baseline_execution_cutover_provenance");

            migrationBuilder.DropTable(
                name: "test_procedure_migration_sources");
        }
    }
}
