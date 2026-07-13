using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExclusiveEditingAndDraftRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClosedBy",
                table: "artifact_edit_sessions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedReason",
                table: "artifact_edit_sessions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "artifact_edit_sessions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<bool>(
                name: "IsExclusive",
                table: "artifact_edit_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LockKey",
                table: "artifact_edit_sessions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Preserve existing experimental editing sessions without making them
            // appear immediately expired when the lease fields are introduced.
            migrationBuilder.Sql("UPDATE artifact_edit_sessions SET \"ExpiresAt\" = \"UpdatedAt\" + INTERVAL '15 minutes';");

            migrationBuilder.CreateTable(
                name: "artifact_draft_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    DraftJson = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RestoredBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RestoredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_draft_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_artifact_draft_snapshots_artifact_edit_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "artifact_edit_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_edit_sessions_LockKey",
                table: "artifact_edit_sessions",
                column: "LockKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_artifact_draft_snapshots_ProjectId_ArtifactType_ArtifactId_~",
                table: "artifact_draft_snapshots",
                columns: new[] { "ProjectId", "ArtifactType", "ArtifactId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_draft_snapshots_SessionId_Sequence",
                table: "artifact_draft_snapshots",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifact_draft_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_artifact_edit_sessions_LockKey",
                table: "artifact_edit_sessions");

            migrationBuilder.DropColumn(
                name: "ClosedBy",
                table: "artifact_edit_sessions");

            migrationBuilder.DropColumn(
                name: "ClosedReason",
                table: "artifact_edit_sessions");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "artifact_edit_sessions");

            migrationBuilder.DropColumn(
                name: "IsExclusive",
                table: "artifact_edit_sessions");

            migrationBuilder.DropColumn(
                name: "LockKey",
                table: "artifact_edit_sessions");
        }
    }
}
