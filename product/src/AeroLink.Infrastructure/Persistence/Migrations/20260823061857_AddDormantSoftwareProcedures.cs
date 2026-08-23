using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDormantSoftwareProcedures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_test_procedure_neutral_artifact_identity",
                table: "test_procedures");

            migrationBuilder.AddColumn<string>(
                name: "Cleanup",
                table: "test_procedure_revisions",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DerivedRationale",
                table: "test_procedure_revisions",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentSetup",
                table: "test_procedure_revisions",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExpectedObservations",
                table: "test_procedure_revisions",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OrderedSteps",
                table: "test_procedure_revisions",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ParentKind",
                table: "test_procedure_revisions",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Unspecified");

            migrationBuilder.AddColumn<string>(
                name: "RetirementRationale",
                table: "test_procedure_revisions",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TestData",
                table: "test_procedure_revisions",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ToolingAutomation",
                table: "test_procedure_revisions",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "test_case_procedure_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcedureRevisionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_case_procedure_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_case_procedure_links_test_procedure_revisions_CaseRevi~",
                        column: x => x.CaseRevisionId,
                        principalTable: "test_procedure_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_case_procedure_links_test_procedure_revisions_Procedur~",
                        column: x => x.ProcedureRevisionId,
                        principalTable: "test_procedure_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_test_procedure_neutral_artifact_identity",
                table: "test_procedures",
                sql: "((\"Level\" = 'System' AND \"ArtifactDiscipline\" = 'System' AND \"ArtifactKind\" = 'Procedure' AND \"BaseNumber\" LIKE 'SYSTP-%') OR (\"Level\" = 'HighLevel' AND \"ArtifactDiscipline\" = 'HighLevelSoftware' AND ((\"ArtifactKind\" = 'Case' AND \"BaseNumber\" LIKE 'HLRTC-%') OR (\"ArtifactKind\" = 'Procedure' AND \"BaseNumber\" LIKE 'HLRTP-%'))) OR (\"Level\" = 'LowLevel' AND \"ArtifactDiscipline\" = 'LowLevelSoftware' AND ((\"ArtifactKind\" = 'Case' AND \"BaseNumber\" LIKE 'LLRTC-%') OR (\"ArtifactKind\" = 'Procedure' AND \"BaseNumber\" LIKE 'LLRTP-%'))))");

            migrationBuilder.CreateIndex(
                name: "IX_test_case_procedure_links_CaseRevisionId",
                table: "test_case_procedure_links",
                column: "CaseRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_test_case_procedure_links_CaseRevisionId_ProcedureRevisionId",
                table: "test_case_procedure_links",
                columns: new[] { "CaseRevisionId", "ProcedureRevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_case_procedure_links_ProcedureRevisionId",
                table: "test_case_procedure_links",
                column: "ProcedureRevisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "test_case_procedure_links");

            migrationBuilder.DropCheckConstraint(
                name: "CK_test_procedure_neutral_artifact_identity",
                table: "test_procedures");

            migrationBuilder.DropColumn(
                name: "Cleanup",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "DerivedRationale",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "EnvironmentSetup",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "ExpectedObservations",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "OrderedSteps",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "ParentKind",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "RetirementRationale",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "TestData",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "ToolingAutomation",
                table: "test_procedure_revisions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_test_procedure_neutral_artifact_identity",
                table: "test_procedures",
                sql: "((\"Level\" = 'System' AND \"ArtifactDiscipline\" = 'System' AND \"ArtifactKind\" = 'Procedure') OR (\"Level\" = 'HighLevel' AND \"ArtifactDiscipline\" = 'HighLevelSoftware' AND \"ArtifactKind\" = 'Case') OR (\"Level\" = 'LowLevel' AND \"ArtifactDiscipline\" = 'LowLevelSoftware' AND \"ArtifactKind\" = 'Case'))");
        }
    }
}
