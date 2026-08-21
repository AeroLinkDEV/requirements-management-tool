using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistProjectLadderConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_ladder_configurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Classification = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActivatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetiredBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_ladder_configurations", x => x.Id);
                    table.UniqueConstraint("AK_project_ladder_configurations_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_project_ladder_configuration_state", "((\"Classification\" = 'LegacyDefault' AND \"State\" = 'Stored' AND \"ActivatedAt\" IS NULL AND \"ActivatedBy\" IS NULL AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Draft' AND \"ActivatedAt\" IS NULL AND \"ActivatedBy\" IS NULL AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Active' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedBy\" IS NOT NULL AND length(trim(\"ActivatedBy\")) > 0 AND \"RetiredAt\" IS NULL AND \"RetiredBy\" IS NULL) OR (\"Classification\" = 'NonDefault' AND \"State\" = 'Retired' AND \"ActivatedAt\" IS NOT NULL AND \"ActivatedBy\" IS NOT NULL AND length(trim(\"ActivatedBy\")) > 0 AND \"RetiredAt\" IS NOT NULL AND \"RetiredBy\" IS NOT NULL AND length(trim(\"RetiredBy\")) > 0))");
                    table.CheckConstraint("CK_project_ladder_configuration_version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_project_ladder_configurations_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_ladder_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogueEntry = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Capabilities = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_ladder_steps", x => x.Id);
                    table.UniqueConstraint("AK_project_ladder_steps_ConfigurationId_ProjectId_Id", x => new { x.ConfigurationId, x.ProjectId, x.Id });
                    table.CheckConstraint("CK_project_ladder_step_capabilities", "\"Capabilities\" >= 0 AND \"Capabilities\" <= 15");
                    table.CheckConstraint("CK_project_ladder_step_position", "\"Position\" > 0");
                    table.CheckConstraint("CK_project_ladder_step_version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_project_ladder_steps_project_ladder_configurations_Configur~",
                        columns: x => new { x.ConfigurationId, x.ProjectId },
                        principalTable: "project_ladder_configurations",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_ladder_steps_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "project_ladder_allowed_upstreams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_ladder_allowed_upstreams", x => x.Id);
                    table.CheckConstraint("CK_project_ladder_upstream_not_self", "\"ParentStepId\" <> \"ChildStepId\"");
                    table.CheckConstraint("CK_project_ladder_upstream_version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_project_ladder_allowed_upstreams_project_ladder_configurati~",
                        columns: x => new { x.ConfigurationId, x.ProjectId },
                        principalTable: "project_ladder_configurations",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_ladder_allowed_upstreams_project_ladder_steps_Confi~",
                        columns: x => new { x.ConfigurationId, x.ProjectId, x.ChildStepId },
                        principalTable: "project_ladder_steps",
                        principalColumns: new[] { "ConfigurationId", "ProjectId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_ladder_allowed_upstreams_project_ladder_steps_Conf~1",
                        columns: x => new { x.ConfigurationId, x.ProjectId, x.ParentStepId },
                        principalTable: "project_ladder_steps",
                        principalColumns: new[] { "ConfigurationId", "ProjectId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_ladder_allowed_upstreams_ConfigurationId_ParentStep~",
                table: "project_ladder_allowed_upstreams",
                columns: new[] { "ConfigurationId", "ParentStepId", "ChildStepId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_ladder_allowed_upstreams_ConfigurationId_ProjectId_~",
                table: "project_ladder_allowed_upstreams",
                columns: new[] { "ConfigurationId", "ProjectId", "ChildStepId" });

            migrationBuilder.CreateIndex(
                name: "IX_project_ladder_allowed_upstreams_ConfigurationId_ProjectId~1",
                table: "project_ladder_allowed_upstreams",
                columns: new[] { "ConfigurationId", "ProjectId", "ParentStepId" });

            migrationBuilder.CreateIndex(
                name: "IX_project_ladder_configurations_ProjectId",
                table: "project_ladder_configurations",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_ladder_steps_ConfigurationId_CatalogueEntry",
                table: "project_ladder_steps",
                columns: new[] { "ConfigurationId", "CatalogueEntry" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_ladder_steps_ConfigurationId_Position",
                table: "project_ladder_steps",
                columns: new[] { "ConfigurationId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_ladder_steps_ProjectId",
                table: "project_ladder_steps",
                column: "ProjectId");

            // The persisted shape is intentionally populated for every existing project.  The deterministic
            // identifiers and conflict guards make the data portion safe to replay when a deployment retries.
            migrationBuilder.Sql("""
                INSERT INTO "project_ladder_configurations" ("Id", "ProjectId", "Classification", "State", "CreatedAt", "UpdatedAt", "Version")
                SELECT md5(p."Id"::text || ':ladder')::uuid, p."Id", 'LegacyDefault', 'Stored', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 1
                FROM "projects" p
                ON CONFLICT ("ProjectId") DO NOTHING;

                INSERT INTO "project_ladder_steps" ("Id", "ConfigurationId", "ProjectId", "CatalogueEntry", "Position", "Capabilities", "CreatedAt", "UpdatedAt", "Version")
                SELECT md5(p."Id"::text || ':step:System')::uuid, c."Id", p."Id", 'System', 1, 7, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 1
                FROM "projects" p
                JOIN "project_ladder_configurations" c ON c."ProjectId" = p."Id"
                ON CONFLICT ("ConfigurationId", "CatalogueEntry") DO NOTHING;

                INSERT INTO "project_ladder_steps" ("Id", "ConfigurationId", "ProjectId", "CatalogueEntry", "Position", "Capabilities", "CreatedAt", "UpdatedAt", "Version")
                SELECT md5(p."Id"::text || ':step:HighLevel')::uuid, c."Id", p."Id", 'HighLevel', 2, 7, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 1
                FROM "projects" p
                JOIN "project_ladder_configurations" c ON c."ProjectId" = p."Id"
                ON CONFLICT ("ConfigurationId", "CatalogueEntry") DO NOTHING;

                INSERT INTO "project_ladder_steps" ("Id", "ConfigurationId", "ProjectId", "CatalogueEntry", "Position", "Capabilities", "CreatedAt", "UpdatedAt", "Version")
                SELECT md5(p."Id"::text || ':step:LowLevel')::uuid, c."Id", p."Id", 'LowLevel', 3, 15, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 1
                FROM "projects" p
                JOIN "project_ladder_configurations" c ON c."ProjectId" = p."Id"
                ON CONFLICT ("ConfigurationId", "CatalogueEntry") DO NOTHING;

                INSERT INTO "project_ladder_allowed_upstreams" ("Id", "ConfigurationId", "ProjectId", "ParentStepId", "ChildStepId", "CreatedAt", "UpdatedAt", "Version")
                SELECT md5(p."Id"::text || ':edge:System:HighLevel')::uuid, c."Id", p."Id", parent."Id", child."Id", CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 1
                FROM "projects" p
                JOIN "project_ladder_configurations" c ON c."ProjectId" = p."Id"
                JOIN "project_ladder_steps" parent ON parent."ConfigurationId" = c."Id" AND parent."ProjectId" = p."Id" AND parent."CatalogueEntry" = 'System'
                JOIN "project_ladder_steps" child ON child."ConfigurationId" = c."Id" AND child."ProjectId" = p."Id" AND child."CatalogueEntry" = 'HighLevel'
                ON CONFLICT ("ConfigurationId", "ParentStepId", "ChildStepId") DO NOTHING;

                INSERT INTO "project_ladder_allowed_upstreams" ("Id", "ConfigurationId", "ProjectId", "ParentStepId", "ChildStepId", "CreatedAt", "UpdatedAt", "Version")
                SELECT md5(p."Id"::text || ':edge:HighLevel:LowLevel')::uuid, c."Id", p."Id", parent."Id", child."Id", CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 1
                FROM "projects" p
                JOIN "project_ladder_configurations" c ON c."ProjectId" = p."Id"
                JOIN "project_ladder_steps" parent ON parent."ConfigurationId" = c."Id" AND parent."ProjectId" = p."Id" AND parent."CatalogueEntry" = 'HighLevel'
                JOIN "project_ladder_steps" child ON child."ConfigurationId" = c."Id" AND child."ProjectId" = p."Id" AND child."CatalogueEntry" = 'LowLevel'
                ON CONFLICT ("ConfigurationId", "ParentStepId", "ChildStepId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_ladder_allowed_upstreams");

            migrationBuilder.DropTable(
                name: "project_ladder_steps");

            migrationBuilder.DropTable(
                name: "project_ladder_configurations");
        }
    }
}
