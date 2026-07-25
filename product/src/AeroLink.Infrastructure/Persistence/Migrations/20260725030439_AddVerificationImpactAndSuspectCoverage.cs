using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationImpactAndSuspectCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmedAt",
                table: "test_requirement_coverage",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmedBy",
                table: "test_requirement_coverage",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSuspect",
                table: "test_requirement_coverage",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SuspectReason",
                table: "test_requirement_coverage",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SuspectSince",
                table: "test_requirement_coverage",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "verification_impact_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequirementChangeId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequirementRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProcedureId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubjectDisplayNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DeclaredVerificationMethod = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AssignedEngineerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AssignedByLeadId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ResolutionRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ResolvedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_impact_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_verification_impact_items_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_verification_impact_items_system_change_requests_ChangeRequ~",
                        column: x => x.ChangeRequestId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_test_requirement_coverage_IsSuspect",
                table: "test_requirement_coverage",
                column: "IsSuspect");

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_items_AssignedEngineerId",
                table: "verification_impact_items",
                column: "AssignedEngineerId");

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_items_ChangeRequestId",
                table: "verification_impact_items",
                column: "ChangeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_items_ProjectId",
                table: "verification_impact_items",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_items_ReleaseId_State",
                table: "verification_impact_items",
                columns: new[] { "ReleaseId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_items_RequirementChangeId",
                table: "verification_impact_items",
                column: "RequirementChangeId");

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_items_RequirementRevisionId",
                table: "verification_impact_items",
                column: "RequirementRevisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "verification_impact_items");

            migrationBuilder.DropIndex(
                name: "IX_test_requirement_coverage_IsSuspect",
                table: "test_requirement_coverage");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "test_requirement_coverage");

            migrationBuilder.DropColumn(
                name: "ConfirmedBy",
                table: "test_requirement_coverage");

            migrationBuilder.DropColumn(
                name: "IsSuspect",
                table: "test_requirement_coverage");

            migrationBuilder.DropColumn(
                name: "SuspectReason",
                table: "test_requirement_coverage");

            migrationBuilder.DropColumn(
                name: "SuspectSince",
                table: "test_requirement_coverage");
        }
    }
}
