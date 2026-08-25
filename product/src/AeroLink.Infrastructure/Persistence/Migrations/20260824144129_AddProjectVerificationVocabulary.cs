using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectVerificationVocabulary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_verification_vocabularies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_verification_vocabularies", x => x.Id);
                    table.UniqueConstraint("AK_project_verification_vocabularies_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.ForeignKey(
                        name: "FK_project_verification_vocabularies_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_verification_methods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VocabularyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    DisplayValue = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedValue = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_verification_methods", x => x.Id);
                    table.CheckConstraint("CK_project_verification_method_position", "\"Position\" > 0");
                    table.ForeignKey(
                        name: "FK_project_verification_methods_project_verification_vocabular~",
                        columns: x => new { x.VocabularyId, x.ProjectId },
                        principalTable: "project_verification_vocabularies",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_verification_methods_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_verification_methods_ProjectId_NormalizedValue",
                table: "project_verification_methods",
                columns: new[] { "ProjectId", "NormalizedValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_verification_methods_VocabularyId_Position",
                table: "project_verification_methods",
                columns: new[] { "VocabularyId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_project_verification_methods_VocabularyId_ProjectId",
                table: "project_verification_methods",
                columns: new[] { "VocabularyId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_project_verification_vocabularies_Id_ProjectId",
                table: "project_verification_vocabularies",
                columns: new[] { "Id", "ProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_verification_vocabularies_ProjectId",
                table: "project_verification_vocabularies",
                column: "ProjectId",
                unique: true);

            // Every project that exists at migration time is founded on the product's verification-method
            // contract, in the order authoring has always offered it. Deliberately NOT derived from the
            // distinct values already stored on requirements: a project holding "Test", "test" and "Testing"
            // would have all three blessed as configured vocabulary, which is exactly the fragmentation #701
            // exists to correct. Whatever a stored record says stays exactly as stored; anything the founding
            // set does not permit is surfaced by the reconciliation report for a deliberate programme
            // decision, never rewritten here or anywhere else.
            //
            // The NOT EXISTS guards make both statements idempotent, so re-running against a database that
            // already carries vocabularies -- restore drills, retried migrations, a project the application
            // created between the two statements -- changes nothing.
            migrationBuilder.Sql("""
                INSERT INTO "project_verification_vocabularies" ("Id", "ProjectId", "CreatedAt", "UpdatedAt", "Version")
                SELECT gen_random_uuid(), p."Id", NOW(), NOW(), 1
                FROM "projects" p
                WHERE NOT EXISTS (
                    SELECT 1 FROM "project_verification_vocabularies" v WHERE v."ProjectId" = p."Id");
                """);
            migrationBuilder.Sql("""
                INSERT INTO "project_verification_methods"
                    ("Id", "VocabularyId", "ProjectId", "Position", "DisplayValue", "NormalizedValue", "CreatedAt", "UpdatedAt", "Version")
                SELECT gen_random_uuid(), v."Id", v."ProjectId", founding."Position", founding."Display",
                       lower(btrim(founding."Display")), v."CreatedAt", v."CreatedAt", 1
                FROM "project_verification_vocabularies" v
                CROSS JOIN (VALUES (1, 'Test'), (2, 'Analysis'), (3, 'Inspection'), (4, 'Demonstration'))
                    AS founding("Position", "Display")
                WHERE NOT EXISTS (
                    SELECT 1 FROM "project_verification_methods" m
                    WHERE m."VocabularyId" = v."Id" AND m."NormalizedValue" = lower(btrim(founding."Display")));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // These tables carry project configuration, not controlled history: dropping them removes what a
            // project permits, never what a requirement declared. Every requirement_change and
            // requirement_revision row is untouched in both directions, which is the #701 posture --
            // configuration can be rolled back; a controlled record's declared method is never rewritten by a
            // migration at all.
            migrationBuilder.DropTable(
                name: "project_verification_methods");

            migrationBuilder.DropTable(
                name: "project_verification_vocabularies");
        }
    }
}
