using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUniversalControlledCheckInEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "controlled_artifact_check_in_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Adapter = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionVersion = table.Column<long>(type: "bigint", nullable: false),
                    BaseSnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResultingSnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AggregateVersionBefore = table.Column<long>(type: "bigint", nullable: false),
                    AggregateVersionAfter = table.Column<long>(type: "bigint", nullable: true),
                    RevisionBefore = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RevisionAfter = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    DraftSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    DraftSequence = table.Column<long>(type: "bigint", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_controlled_artifact_check_in_evidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_controlled_artifact_check_in_evidence_artifact_draft_snapsh~",
                        column: x => x.DraftSnapshotId,
                        principalTable: "artifact_draft_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_controlled_artifact_check_in_evidence_artifact_edit_session~",
                        column: x => x.SessionId,
                        principalTable: "artifact_edit_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_controlled_artifact_check_in_evidence_DraftSnapshotId",
                table: "controlled_artifact_check_in_evidence",
                column: "DraftSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_controlled_artifact_check_in_evidence_ProjectId_ArtifactTyp~",
                table: "controlled_artifact_check_in_evidence",
                columns: new[] { "ProjectId", "ArtifactType", "ArtifactId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_controlled_artifact_check_in_evidence_SessionId_OccurredAt",
                table: "controlled_artifact_check_in_evidence",
                columns: new[] { "SessionId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "controlled_artifact_check_in_evidence");
        }
    }
}
