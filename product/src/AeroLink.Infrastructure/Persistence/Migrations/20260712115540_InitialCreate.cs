using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidate_baselines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredecessorBaselineId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_baselines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "programs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_programs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SoftwareProduct = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "software_releases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsReleased = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_software_releases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "system_change_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Problem = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Analysis = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Solution = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    AuthorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_change_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "baseline_scr_selections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScrId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScrDisplayNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baseline_scr_selections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_baseline_scr_selections_candidate_baselines_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audit_events_system_change_requests_AggregateId",
                        column: x => x.AggregateId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "requirement_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScrId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Statement = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    VerificationMethod = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirement_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_requirement_changes_system_change_requests_ScrId",
                        column: x => x.ScrId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_cycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScrId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_cycles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_review_cycles_system_change_requests_ScrId",
                        column: x => x.ScrId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "approval_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewCycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ApproverId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApproverName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_approval_steps_review_cycles_ReviewCycleId",
                        column: x => x.ReviewCycleId,
                        principalTable: "review_cycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approval_steps_ReviewCycleId_Position",
                table: "approval_steps",
                columns: new[] { "ReviewCycleId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_AggregateId_OccurredAt",
                table: "audit_events",
                columns: new[] { "AggregateId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_baseline_scr_selections_BaselineId_ScrId",
                table: "baseline_scr_selections",
                columns: new[] { "BaselineId", "ScrId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_candidate_baselines_BaseNumber_Revision",
                table: "candidate_baselines",
                columns: new[] { "BaseNumber", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_programs_Code",
                table: "programs",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_ProgramId_Name",
                table: "projects",
                columns: new[] { "ProgramId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requirement_changes_ScrId_BaseNumber_Revision",
                table: "requirement_changes",
                columns: new[] { "ScrId", "BaseNumber", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_cycles_ScrId_Sequence",
                table: "review_cycles",
                columns: new[] { "ScrId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_software_releases_ProjectId_Version",
                table: "software_releases",
                columns: new[] { "ProjectId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_system_change_requests_BaseNumber_Revision",
                table: "system_change_requests",
                columns: new[] { "BaseNumber", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_steps");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "baseline_scr_selections");

            migrationBuilder.DropTable(
                name: "programs");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "requirement_changes");

            migrationBuilder.DropTable(
                name: "software_releases");

            migrationBuilder.DropTable(
                name: "review_cycles");

            migrationBuilder.DropTable(
                name: "candidate_baselines");

            migrationBuilder.DropTable(
                name: "system_change_requests");
        }
    }
}
