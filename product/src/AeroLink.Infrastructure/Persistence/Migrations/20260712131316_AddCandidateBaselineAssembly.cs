using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCandidateBaselineAssembly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "candidate_baselines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FrozenAt",
                table: "candidate_baselines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "candidate_baselines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.CreateTable(
                name: "baseline_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baseline_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_baseline_events_candidate_baselines_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_baselines_ProjectId_ReleaseId_State",
                table: "candidate_baselines",
                columns: new[] { "ProjectId", "ReleaseId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_baseline_events_BaselineId_OccurredAt",
                table: "baseline_events",
                columns: new[] { "BaselineId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "baseline_events");

            migrationBuilder.DropIndex(
                name: "IX_candidate_baselines_ProjectId_ReleaseId_State",
                table: "candidate_baselines");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "candidate_baselines");

            migrationBuilder.DropColumn(
                name: "FrozenAt",
                table: "candidate_baselines");

            migrationBuilder.DropColumn(
                name: "State",
                table: "candidate_baselines");
        }
    }
}
