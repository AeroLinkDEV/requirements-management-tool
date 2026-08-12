using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectPersonnelAndStandingBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_program_memberships_ProgramId",
                table: "program_memberships");

            migrationBuilder.DropIndex(
                name: "IX_program_memberships_UserId_ProgramId_Role",
                table: "program_memberships");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndedAt",
                table: "program_memberships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndedBy",
                table: "program_memberships",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "project_role_backups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BackupUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    NamedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NamedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_role_backups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_role_backups_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_role_backups_user_accounts_BackupUserId",
                        column: x => x.BackupUserId,
                        principalTable: "user_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_program_memberships_ProgramId_EndedAt",
                table: "program_memberships",
                columns: new[] { "ProgramId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_program_memberships_UserId_ProgramId_Role",
                table: "program_memberships",
                columns: new[] { "UserId", "ProgramId", "Role" },
                unique: true,
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_project_role_backups_BackupUserId",
                table: "project_role_backups",
                column: "BackupUserId");

            migrationBuilder.CreateIndex(
                name: "IX_project_role_backups_ProgramId_BackupUserId_RemovedAt",
                table: "project_role_backups",
                columns: new[] { "ProgramId", "BackupUserId", "RemovedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_project_role_backups_ProgramId_Role",
                table: "project_role_backups",
                columns: new[] { "ProgramId", "Role" },
                unique: true,
                filter: "\"RemovedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_role_backups");

            migrationBuilder.DropIndex(
                name: "IX_program_memberships_ProgramId_EndedAt",
                table: "program_memberships");

            migrationBuilder.DropIndex(
                name: "IX_program_memberships_UserId_ProgramId_Role",
                table: "program_memberships");

            migrationBuilder.DropColumn(
                name: "EndedAt",
                table: "program_memberships");

            migrationBuilder.DropColumn(
                name: "EndedBy",
                table: "program_memberships");

            migrationBuilder.CreateIndex(
                name: "IX_program_memberships_ProgramId",
                table: "program_memberships",
                column: "ProgramId");

            migrationBuilder.CreateIndex(
                name: "IX_program_memberships_UserId_ProgramId_Role",
                table: "program_memberships",
                columns: new[] { "UserId", "ProgramId", "Role" },
                unique: true);
        }
    }
}
