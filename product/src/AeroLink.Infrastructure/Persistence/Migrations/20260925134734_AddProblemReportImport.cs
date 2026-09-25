using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProblemReportImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClosedInSource",
                table: "problem_reports",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SourceCreatedAt",
                table: "problem_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "problem_reports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceReportedBy",
                table: "problem_reports",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceState",
                table: "problem_reports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                table: "problem_reports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "problem_report_import_batches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MappingJson = table.Column<string>(type: "text", nullable: false),
                    PreviewHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Created = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    ImportedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_problem_report_import_batches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_problem_report_import_batches_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_problem_reports_ProjectId_SourceSystem_SourceKey",
                table: "problem_reports",
                columns: new[] { "ProjectId", "SourceSystem", "SourceKey" });

            migrationBuilder.CreateIndex(
                name: "IX_problem_report_import_batches_ProjectId",
                table: "problem_report_import_batches",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "problem_report_import_batches");

            migrationBuilder.DropIndex(
                name: "IX_problem_reports_ProjectId_SourceSystem_SourceKey",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "ClosedInSource",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "SourceCreatedAt",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "SourceReportedBy",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "SourceState",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                table: "problem_reports");
        }
    }
}
