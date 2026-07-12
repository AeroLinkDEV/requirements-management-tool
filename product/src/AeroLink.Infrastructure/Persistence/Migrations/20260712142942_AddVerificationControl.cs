using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "test_procedures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_procedures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_procedures_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "test_procedure_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcedureId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Objective = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Preconditions = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Steps = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    ExpectedResult = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AuthorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_procedure_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_procedure_revisions_test_procedures_ProcedureId",
                        column: x => x.ProcedureId,
                        principalTable: "test_procedures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "test_executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcedureRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SoftwareBuildId = table.Column<Guid>(type: "uuid", nullable: true),
                    RetestOfExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ExecutedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Configuration = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Determination = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_executions_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_executions_software_builds_SoftwareBuildId",
                        column: x => x.SoftwareBuildId,
                        principalTable: "software_builds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_executions_test_executions_RetestOfExecutionId",
                        column: x => x.RetestOfExecutionId,
                        principalTable: "test_executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_executions_test_procedure_revisions_ProcedureRevisionId",
                        column: x => x.ProcedureRevisionId,
                        principalTable: "test_procedure_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "test_requirement_coverage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcedureRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementRevisionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_requirement_coverage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_requirement_coverage_requirement_revisions_Requirement~",
                        column: x => x.RequirementRevisionId,
                        principalTable: "requirement_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_requirement_coverage_test_procedure_revisions_Procedur~",
                        column: x => x.ProcedureRevisionId,
                        principalTable: "test_procedure_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_test_executions_ProcedureRevisionId",
                table: "test_executions",
                column: "ProcedureRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_test_executions_ProjectId_ExecutedAt",
                table: "test_executions",
                columns: new[] { "ProjectId", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_test_executions_RetestOfExecutionId",
                table: "test_executions",
                column: "RetestOfExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_test_executions_SoftwareBuildId",
                table: "test_executions",
                column: "SoftwareBuildId");

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_revisions_ProcedureId_Revision",
                table: "test_procedure_revisions",
                columns: new[] { "ProcedureId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_procedures_ProjectId_BaseNumber",
                table: "test_procedures",
                columns: new[] { "ProjectId", "BaseNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_requirement_coverage_ProcedureRevisionId_RequirementRe~",
                table: "test_requirement_coverage",
                columns: new[] { "ProcedureRevisionId", "RequirementRevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_requirement_coverage_RequirementRevisionId",
                table: "test_requirement_coverage",
                column: "RequirementRevisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "test_executions");

            migrationBuilder.DropTable(
                name: "test_requirement_coverage");

            migrationBuilder.DropTable(
                name: "test_procedure_revisions");

            migrationBuilder.DropTable(
                name: "test_procedures");
        }
    }
}
