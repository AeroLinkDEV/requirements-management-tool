using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorProjectLadderConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_project_ladder_configuration_state",
                table: "project_ladder_configurations");

            migrationBuilder.AddColumn<string>(
                name: "ActivationManifestHash",
                table: "project_ladder_configurations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivationManifestVersion",
                table: "project_ladder_configurations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_ladder_configuration_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CanonicalSnapshot = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_ladder_configuration_history", x => x.Id);
                    table.CheckConstraint("CK_project_ladder_history_evidence", "length(\"Actor\") > 0 AND length(\"Reason\") > 0 AND length(\"CanonicalSnapshot\") > 0 AND length(\"SnapshotHash\") > 0");
                    table.CheckConstraint("CK_project_ladder_history_revision", "\"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_project_ladder_configuration_history_project_ladder_configu~",
                        columns: x => new { x.ConfigurationId, x.ProjectId },
                        principalTable: "project_ladder_configurations",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_project_ladder_configuration_state",
                table: "project_ladder_configurations",
                sql: "((\"Classification\" = 'LegacyDefault' AND \"State\" = 'Stored' AND \"ActivatedAt\" IS NULL AND \"ActivatedBy\" IS NULL AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL AND \"ActivationManifestVersion\" IS NULL AND \"ActivationManifestHash\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Draft' AND \"ActivatedAt\" IS NULL AND \"ActivatedBy\" IS NULL AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL AND \"ActivationManifestVersion\" IS NULL AND \"ActivationManifestHash\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Active' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedBy\" IS NOT NULL AND length(trim(\"ActivatedBy\")) > 0 AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL AND \"ActivationManifestVersion\" IS NOT NULL AND length(trim(\"ActivationManifestVersion\")) > 0 AND \"ActivationManifestHash\" IS NOT NULL AND length(trim(\"ActivationManifestHash\")) > 0) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Retired' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedBy\" IS NOT NULL AND length(trim(\"ActivatedBy\")) > 0 AND \"RetiredAt\" IS NOT NULL AND \"RetiredBy\" IS NOT NULL AND length(trim(\"RetiredBy\")) > 0 AND \"ActivationManifestVersion\" IS NOT NULL AND length(trim(\"ActivationManifestVersion\")) > 0 AND \"ActivationManifestHash\" IS NOT NULL AND length(trim(\"ActivationManifestHash\")) > 0))");

            migrationBuilder.CreateIndex(
                name: "IX_project_ladder_configuration_history_ConfigurationId_Projec~",
                table: "project_ladder_configuration_history",
                columns: new[] { "ConfigurationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_project_ladder_configuration_history_ConfigurationId_Revisi~",
                table: "project_ladder_configuration_history",
                columns: new[] { "ConfigurationId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_ladder_configuration_history_ProjectId_OccurredAt",
                table: "project_ladder_configuration_history",
                columns: new[] { "ProjectId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_ladder_configuration_history");

            migrationBuilder.DropCheckConstraint(
                name: "CK_project_ladder_configuration_state",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "ActivationManifestHash",
                table: "project_ladder_configurations");

            migrationBuilder.DropColumn(
                name: "ActivationManifestVersion",
                table: "project_ladder_configurations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_project_ladder_configuration_state",
                table: "project_ladder_configurations",
                sql: "((\"Classification\" = 'LegacyDefault' AND \"State\" = 'Stored' AND \"ActivatedAt\" IS NULL AND \"ActivatedBy\" IS NULL AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Draft' AND \"ActivatedAt\" IS NULL AND \"ActivatedBy\" IS NULL AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Active' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedBy\" IS NOT NULL AND length(trim(\"ActivatedBy\")) > 0 AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Retired' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedBy\" IS NOT NULL AND length(trim(\"ActivatedBy\")) > 0 AND \"RetiredAt\" IS NOT NULL AND \"RetiredBy\" IS NOT NULL AND length(trim(\"RetiredBy\")) > 0))");
        }
    }
}
