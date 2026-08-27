using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectLeadership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_leadership_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HolderUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_leadership_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_leadership_assignments_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_leadership_assignments_user_accounts_HolderUserId",
                        column: x => x.HolderUserId,
                        principalTable: "user_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "project_leadership_backups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BackupUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    NamedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NamedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_leadership_backups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_leadership_backups_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_leadership_backups_user_accounts_BackupUserId",
                        column: x => x.BackupUserId,
                        principalTable: "user_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_leadership_assignments_HolderUserId_EndedAt",
                table: "project_leadership_assignments",
                columns: new[] { "HolderUserId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_project_leadership_assignments_ProgramId_Position",
                table: "project_leadership_assignments",
                columns: new[] { "ProgramId", "Position" },
                unique: true,
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_project_leadership_backups_BackupUserId_RemovedAt",
                table: "project_leadership_backups",
                columns: new[] { "BackupUserId", "RemovedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_project_leadership_backups_ProgramId_Position",
                table: "project_leadership_backups",
                columns: new[] { "ProgramId", "Position" },
                unique: true,
                filter: "\"RemovedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_leadership_assignments");

            migrationBuilder.DropTable(
                name: "project_leadership_backups");
        }
    }
}
