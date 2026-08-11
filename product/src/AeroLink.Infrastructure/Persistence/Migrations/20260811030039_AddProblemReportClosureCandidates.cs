using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProblemReportClosureCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "problem_report_closure_candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProblemReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportRevision = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    ReportVersion = table.Column<long>(type: "bigint", nullable: false),
                    ReportSnapshotJson = table.Column<string>(type: "text", nullable: false),
                    ReportSnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VerificationExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    VerificationEvidenceJson = table.Column<string>(type: "text", nullable: false),
                    VerificationEvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LinksManifestJson = table.Column<string>(type: "text", nullable: false),
                    LinksManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SelectedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    InvalidatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InvalidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InvalidationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ApprovedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_problem_report_closure_candidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_problem_report_closure_candidates_problem_reports_ProblemRe~",
                        column: x => x.ProblemReportId,
                        principalTable: "problem_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_problem_report_closure_candidates_ManifestHash",
                table: "problem_report_closure_candidates",
                column: "ManifestHash");

            migrationBuilder.CreateIndex(
                name: "IX_problem_report_closure_candidates_ProblemReportId_ReportRev~",
                table: "problem_report_closure_candidates",
                columns: new[] { "ProblemReportId", "ReportRevision", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_problem_report_closure_candidates_ProblemReportId_State",
                table: "problem_report_closure_candidates",
                columns: new[] { "ProblemReportId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "problem_report_closure_candidates");
        }
    }
}
