using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTestProcedureBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EffectiveBaselineId",
                table: "test_procedure_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceTestChangeRequestId",
                table: "test_procedure_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TestProceduresHash",
                table: "candidate_baselines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TestProceduresMaterializedAt",
                table: "candidate_baselines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "baseline_test_change_request_selections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestChangeRequestDisplayNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baseline_test_change_request_selections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_baseline_test_change_request_selections_candidate_baselines~",
                        column: x => x.BaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "baseline_test_procedures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcedureId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baseline_test_procedures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_revisions_SourceTestChangeRequestId",
                table: "test_procedure_revisions",
                column: "SourceTestChangeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_baseline_test_change_request_selections_BaselineId_TestChan~",
                table: "baseline_test_change_request_selections",
                columns: new[] { "BaselineId", "TestChangeRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_baseline_test_procedures_BaselineId_ProcedureId",
                table: "baseline_test_procedures",
                columns: new[] { "BaselineId", "ProcedureId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_baseline_test_procedures_RevisionId",
                table: "baseline_test_procedures",
                column: "RevisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "baseline_test_change_request_selections");

            migrationBuilder.DropTable(
                name: "baseline_test_procedures");

            migrationBuilder.DropIndex(
                name: "IX_test_procedure_revisions_SourceTestChangeRequestId",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "EffectiveBaselineId",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "SourceTestChangeRequestId",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "TestProceduresHash",
                table: "candidate_baselines");

            migrationBuilder.DropColumn(
                name: "TestProceduresMaterializedAt",
                table: "candidate_baselines");
        }
    }
}
