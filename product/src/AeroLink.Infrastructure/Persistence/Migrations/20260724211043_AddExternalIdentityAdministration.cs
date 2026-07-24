using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalIdentityAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_identity_providers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Protocol = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SubjectClaim = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GroupClaim = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_identity_providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "external_group_role_mappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalGroup = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_group_role_mappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_external_group_role_mappings_external_identity_providers_Pr~",
                        column: x => x.ProviderId,
                        principalTable: "external_identity_providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_external_group_role_mappings_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_group_role_mappings_ProgramId",
                table: "external_group_role_mappings",
                column: "ProgramId");

            migrationBuilder.CreateIndex(
                name: "IX_external_group_role_mappings_ProviderId_ExternalGroup_Progr~",
                table: "external_group_role_mappings",
                columns: new[] { "ProviderId", "ExternalGroup", "ProgramId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_identity_providers_Issuer",
                table: "external_identity_providers",
                column: "Issuer",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_identity_providers_Key",
                table: "external_identity_providers",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_group_role_mappings");

            migrationBuilder.DropTable(
                name: "external_identity_providers");
        }
    }
}
