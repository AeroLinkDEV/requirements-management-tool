using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeVerificationProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnabledArtifactKindsValue",
                table: "project_ladder_steps",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "VerificationProfileSchemaVersion",
                table: "project_ladder_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotSchemaVersion",
                table: "project_ladder_configuration_history",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // The migration records the dormant v2 shape only. It does not activate a profile or fabricate
            // readiness/upgrade evidence; existing rows retain their characterized v1 snapshot algorithm.
            migrationBuilder.Sql("UPDATE \"project_ladder_steps\" SET \"EnabledArtifactKindsValue\" = CASE \"CatalogueEntry\" WHEN 'System' THEN 'Procedure' WHEN 'HighLevel' THEN 'Case' WHEN 'LowLevel' THEN 'Case' ELSE '' END");

            migrationBuilder.AddCheckConstraint(
                name: "CK_project_ladder_step_profile_shape",
                table: "project_ladder_steps",
                sql: "((\"Capabilities\" & 2) = 0 AND \"EnabledArtifactKindsValue\" = '') OR ((\"Capabilities\" & 2) = 2 AND ((\"CatalogueEntry\" = 'System' AND \"EnabledArtifactKindsValue\" = 'Procedure') OR (\"CatalogueEntry\" IN ('HighLevel','LowLevel') AND \"EnabledArtifactKindsValue\" IN ('Case','Case,Procedure')) OR (\"CatalogueEntry\" NOT IN ('System','HighLevel','LowLevel') AND \"EnabledArtifactKindsValue\" IN ('Case','Procedure','Case,Procedure',''))))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_project_ladder_profile_schema_version",
                table: "project_ladder_configurations",
                sql: "\"VerificationProfileSchemaVersion\" = 2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_project_ladder_history_schema_version",
                table: "project_ladder_configuration_history",
                sql: "\"SnapshotSchemaVersion\" IN (1,2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_project_ladder_step_profile_shape",
                table: "project_ladder_steps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_project_ladder_profile_schema_version",
                table: "project_ladder_configurations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_project_ladder_history_schema_version",
                table: "project_ladder_configuration_history");

            migrationBuilder.DropColumn(
                name: "EnabledArtifactKindsValue",
                table: "project_ladder_steps");

            migrationBuilder.DropColumn(
                name: "VerificationProfileSchemaVersion",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "SnapshotSchemaVersion",
                table: "project_ladder_configuration_history");
        }
    }
}
