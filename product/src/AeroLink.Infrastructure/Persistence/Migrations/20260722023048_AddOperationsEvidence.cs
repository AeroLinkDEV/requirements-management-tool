using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationsEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_restore_drill_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupLocation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BackupHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BackupCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OffsiteVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TargetRpoMinutes = table.Column<int>(type: "integer", nullable: false),
                    TargetRtoMinutes = table.Column<int>(type: "integer", nullable: false),
                    ActualRpoMinutes = table.Column<int>(type: "integer", nullable: false),
                    ActualRtoMinutes = table.Column<int>(type: "integer", nullable: false),
                    RestoreEnvironment = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ExecutedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_restore_drill_evidence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "operational_alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Severity = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Signal = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    RunbookUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OpenedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operational_alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "retention_policy_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false),
                    LegalHold = table.Column<bool>(type: "boolean", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ConfiguredBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ConfiguredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_policy_evidence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "upgrade_assurance_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ToVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PreflightJson = table.Column<string>(type: "text", nullable: false),
                    PostCheckJson = table.Column<string>(type: "text", nullable: false),
                    EvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ExecutedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upgrade_assurance_evidence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workload_qualification_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequirementCount = table.Column<int>(type: "integer", nullable: false),
                    ConcurrentUsers = table.Column<int>(type: "integer", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    ResultsJson = table.Column<string>(type: "text", nullable: false),
                    ReportHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ExecutedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workload_qualification_evidence", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backup_restore_drill_evidence_ProjectId_ExecutedAt",
                table: "backup_restore_drill_evidence",
                columns: new[] { "ProjectId", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_operational_alerts_ProjectId_State_OpenedAt",
                table: "operational_alerts",
                columns: new[] { "ProjectId", "State", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_retention_policy_evidence_ProjectId_RecordType_ConfiguredAt",
                table: "retention_policy_evidence",
                columns: new[] { "ProjectId", "RecordType", "ConfiguredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_upgrade_assurance_evidence_ProjectId_ExecutedAt",
                table: "upgrade_assurance_evidence",
                columns: new[] { "ProjectId", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workload_qualification_evidence_ProjectId_ExecutedAt",
                table: "workload_qualification_evidence",
                columns: new[] { "ProjectId", "ExecutedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_restore_drill_evidence");

            migrationBuilder.DropTable(
                name: "operational_alerts");

            migrationBuilder.DropTable(
                name: "retention_policy_evidence");

            migrationBuilder.DropTable(
                name: "upgrade_assurance_evidence");

            migrationBuilder.DropTable(
                name: "workload_qualification_evidence");
        }
    }
}
