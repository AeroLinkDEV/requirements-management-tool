using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNeutralVerificationIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtifactDiscipline",
                table: "test_procedures",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "System");

            migrationBuilder.AddColumn<string>(
                name: "ArtifactKind",
                table: "test_procedures",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Procedure");

            migrationBuilder.Sql("UPDATE \"test_procedures\" SET \"ArtifactDiscipline\" = CASE \"Level\" WHEN 'System' THEN 'System' WHEN 'HighLevel' THEN 'HighLevelSoftware' WHEN 'LowLevel' THEN 'LowLevelSoftware' ELSE '' END, \"ArtifactKind\" = CASE \"Level\" WHEN 'System' THEN 'Procedure' WHEN 'HighLevel' THEN 'Case' WHEN 'LowLevel' THEN 'Case' ELSE '' END");

            migrationBuilder.AddCheckConstraint(
                name: "CK_test_procedure_neutral_artifact_identity",
                table: "test_procedures",
                sql: "((\"Level\" = 'System' AND \"ArtifactDiscipline\" = 'System' AND \"ArtifactKind\" = 'Procedure') OR (\"Level\" = 'HighLevel' AND \"ArtifactDiscipline\" = 'HighLevelSoftware' AND \"ArtifactKind\" = 'Case') OR (\"Level\" = 'LowLevel' AND \"ArtifactDiscipline\" = 'LowLevelSoftware' AND \"ArtifactKind\" = 'Case'))");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_test_procedure_neutral_artifact_identity",
                table: "test_procedures");

            migrationBuilder.DropColumn(
                name: "ArtifactDiscipline",
                table: "test_procedures");

            migrationBuilder.DropColumn(
                name: "ArtifactKind",
                table: "test_procedures");

        }
    }
}
