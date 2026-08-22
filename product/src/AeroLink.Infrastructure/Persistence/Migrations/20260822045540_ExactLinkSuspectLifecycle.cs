using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExactLinkSuspectLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExactLinkSuspectLifecycleId",
                table: "requirement_trace_links",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "exact_link_suspect_lifecycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CauseKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CauseRequirementRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CauseBaselineImportId = table.Column<Guid>(type: "uuid", nullable: true),
                    RaisedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RaisedRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgementRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolutionRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exact_link_suspect_lifecycles", x => x.Id);
                    table.CheckConstraint("CK_exact_link_suspect_lifecycle_cause_xor", "((\"CauseKind\" = 'InternalRequirementRevision' AND \"CauseRequirementRevisionId\" IS NOT NULL AND \"CauseBaselineImportId\" IS NULL) OR (\"CauseKind\" = 'ExternalBaselineImport' AND \"CauseRequirementRevisionId\" IS NULL AND \"CauseBaselineImportId\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_exact_link_suspect_lifecycles_baseline_imports_CauseBaselin~",
                        column: x => x.CauseBaselineImportId,
                        principalTable: "baseline_imports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_exact_link_suspect_lifecycles_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_exact_link_suspect_lifecycles_requirement_revisions_CauseRe~",
                        column: x => x.CauseRequirementRevisionId,
                        principalTable: "requirement_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "exact_link_suspect_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LifecycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CauseKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CauseRequirementRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CauseBaselineImportId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exact_link_suspect_events", x => x.Id);
                    table.CheckConstraint("CK_exact_link_suspect_event_cause_xor", "((\"CauseKind\" = 'InternalRequirementRevision' AND \"CauseRequirementRevisionId\" IS NOT NULL AND \"CauseBaselineImportId\" IS NULL) OR (\"CauseKind\" = 'ExternalBaselineImport' AND \"CauseRequirementRevisionId\" IS NULL AND \"CauseBaselineImportId\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_exact_link_suspect_events_baseline_imports_CauseBaselineImp~",
                        column: x => x.CauseBaselineImportId,
                        principalTable: "baseline_imports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_exact_link_suspect_events_exact_link_suspect_lifecycles_Lif~",
                        column: x => x.LifecycleId,
                        principalTable: "exact_link_suspect_lifecycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_exact_link_suspect_events_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_exact_link_suspect_events_requirement_revisions_CauseRequir~",
                        column: x => x.CauseRequirementRevisionId,
                        principalTable: "requirement_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_requirement_trace_links_ExactLinkSuspectLifecycleId",
                table: "requirement_trace_links",
                column: "ExactLinkSuspectLifecycleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_events_CauseBaselineImportId",
                table: "exact_link_suspect_events",
                column: "CauseBaselineImportId");

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_events_CauseRequirementRevisionId",
                table: "exact_link_suspect_events",
                column: "CauseRequirementRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_events_LifecycleId",
                table: "exact_link_suspect_events",
                column: "LifecycleId");

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_events_LinkKind_LinkId_OccurredAt",
                table: "exact_link_suspect_events",
                columns: new[] { "LinkKind", "LinkId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_events_ProjectId",
                table: "exact_link_suspect_events",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_lifecycles_CauseBaselineImportId",
                table: "exact_link_suspect_lifecycles",
                column: "CauseBaselineImportId");

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_lifecycles_CauseRequirementRevisionId",
                table: "exact_link_suspect_lifecycles",
                column: "CauseRequirementRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_lifecycles_LinkKind_LinkId",
                table: "exact_link_suspect_lifecycles",
                columns: new[] { "LinkKind", "LinkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exact_link_suspect_lifecycles_ProjectId_State",
                table: "exact_link_suspect_lifecycles",
                columns: new[] { "ProjectId", "State" });

            migrationBuilder.AddForeignKey(
                name: "FK_requirement_trace_links_exact_link_suspect_lifecycles_Exact~",
                table: "requirement_trace_links",
                column: "ExactLinkSuspectLifecycleId",
                principalTable: "exact_link_suspect_lifecycles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_requirement_trace_links_exact_link_suspect_lifecycles_Exact~",
                table: "requirement_trace_links");

            migrationBuilder.DropTable(
                name: "exact_link_suspect_events");

            migrationBuilder.DropTable(
                name: "exact_link_suspect_lifecycles");

            migrationBuilder.DropIndex(
                name: "IX_requirement_trace_links_ExactLinkSuspectLifecycleId",
                table: "requirement_trace_links");

            migrationBuilder.DropColumn(
                name: "ExactLinkSuspectLifecycleId",
                table: "requirement_trace_links");
        }
    }
}
