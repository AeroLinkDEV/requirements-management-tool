using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftwareProcedureProposalVocabulary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Cleanup",
                table: "test_procedure_changes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentSetup",
                table: "test_procedure_changes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExpectedObservations",
                table: "test_procedure_changes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OrderedSteps",
                table: "test_procedure_changes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TestData",
                table: "test_procedure_changes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ToolingAutomation",
                table: "test_procedure_changes",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cleanup",
                table: "test_procedure_changes");

            migrationBuilder.DropColumn(
                name: "EnvironmentSetup",
                table: "test_procedure_changes");

            migrationBuilder.DropColumn(
                name: "ExpectedObservations",
                table: "test_procedure_changes");

            migrationBuilder.DropColumn(
                name: "OrderedSteps",
                table: "test_procedure_changes");

            migrationBuilder.DropColumn(
                name: "TestData",
                table: "test_procedure_changes");

            migrationBuilder.DropColumn(
                name: "ToolingAutomation",
                table: "test_procedure_changes");
        }
    }
}
