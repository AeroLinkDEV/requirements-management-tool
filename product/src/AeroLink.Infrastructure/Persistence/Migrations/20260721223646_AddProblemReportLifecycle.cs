using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProblemReportLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "State",
                table: "problem_reports",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30);

            migrationBuilder.AddColumn<string>(
                name: "AffectedConfiguration",
                table: "problem_reports",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Classification",
                table: "problem_reports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosureApprovedAt",
                table: "problem_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClosureApprovedBy",
                table: "problem_reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosureApprovedByName",
                table: "problem_reports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Containment",
                table: "problem_reports",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CorrectiveAction",
                table: "problem_reports",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Disposition",
                table: "problem_reports",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DispositionRationale",
                table: "problem_reports",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Effects",
                table: "problem_reports",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsReleaseBlocker",
                table: "problem_reports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "problem_reports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "problem_reports",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ResolutionVerificationExecutionId",
                table: "problem_reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "problem_reports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RootCause",
                table: "problem_reports",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "problem_reports",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WaivedAt",
                table: "problem_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaivedBy",
                table: "problem_reports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WaiverRationale",
                table: "problem_reports",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "problem_report_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProblemReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Relationship = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    AddedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_problem_report_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_problem_report_links_problem_reports_ProblemReportId",
                        column: x => x.ProblemReportId,
                        principalTable: "problem_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "problem_report_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProblemReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_problem_report_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_problem_report_revisions_problem_reports_ProblemReportId",
                        column: x => x.ProblemReportId,
                        principalTable: "problem_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_problem_reports_ProjectId_IsReleaseBlocker",
                table: "problem_reports",
                columns: new[] { "ProjectId", "IsReleaseBlocker" });

            migrationBuilder.CreateIndex(
                name: "IX_problem_reports_ProjectId_State_Severity",
                table: "problem_reports",
                columns: new[] { "ProjectId", "State", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_problem_report_links_ArtifactType_ArtifactId",
                table: "problem_report_links",
                columns: new[] { "ArtifactType", "ArtifactId" });

            migrationBuilder.CreateIndex(
                name: "IX_problem_report_links_ProblemReportId_ArtifactType_ArtifactI~",
                table: "problem_report_links",
                columns: new[] { "ProblemReportId", "ArtifactType", "ArtifactId", "Relationship" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_problem_report_revisions_ProblemReportId_OccurredAt",
                table: "problem_report_revisions",
                columns: new[] { "ProblemReportId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_problem_report_revisions_ProblemReportId_Revision_EventType",
                table: "problem_report_revisions",
                columns: new[] { "ProblemReportId", "Revision", "EventType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "problem_report_links");

            migrationBuilder.DropTable(
                name: "problem_report_revisions");

            migrationBuilder.DropIndex(
                name: "IX_problem_reports_ProjectId_IsReleaseBlocker",
                table: "problem_reports");

            migrationBuilder.DropIndex(
                name: "IX_problem_reports_ProjectId_State_Severity",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "AffectedConfiguration",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "Classification",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "ClosureApprovedAt",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "ClosureApprovedBy",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "ClosureApprovedByName",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "Containment",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "CorrectiveAction",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "Disposition",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "DispositionRationale",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "Effects",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "IsReleaseBlocker",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "ResolutionVerificationExecutionId",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "RootCause",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "WaivedAt",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "WaivedBy",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "WaiverRationale",
                table: "problem_reports");

            migrationBuilder.AlterColumn<string>(
                name: "State",
                table: "problem_reports",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40);
        }
    }
}
