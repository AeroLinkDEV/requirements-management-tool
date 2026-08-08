using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProcedureCoverageDeltas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverageChangeRationale",
                table: "test_procedure_changes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CoverageChangedBy",
                table: "test_procedure_changes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RemovedRequirementRevisionIdsJson",
                table: "test_procedure_changes",
                type: "text",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverageChangeRationale",
                table: "test_procedure_changes");

            migrationBuilder.DropColumn(
                name: "CoverageChangedBy",
                table: "test_procedure_changes");

            migrationBuilder.DropColumn(
                name: "RemovedRequirementRevisionIdsJson",
                table: "test_procedure_changes");
        }
    }
}
