using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDownstreamAssessmentsAndSelectedApprovers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedApproverId",
                table: "test_procedure_revisions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectedApproverId",
                table: "test_change_reviews",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByTestChangeRequestId",
                table: "test_change_reviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupersededReason",
                table: "test_change_reviews",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "downstream_change_assessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceChangeRequestNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetLevel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AssignedEngineerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SelectedApproverId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededByAssessmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_downstream_change_assessments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_downstream_change_assessments_downstream_change_assessments~",
                        column: x => x.SupersededByAssessmentId,
                        principalTable: "downstream_change_assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_downstream_change_assessments_system_change_requests_Source~",
                        column: x => x.SourceChangeRequestId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "downstream_assessment_change_request_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeRequestNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LinkedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_downstream_assessment_change_request_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_downstream_assessment_change_request_links_downstream_chang~",
                        column: x => x.AssessmentId,
                        principalTable: "downstream_change_assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_downstream_assessment_change_request_links_system_change_re~",
                        column: x => x.ChangeRequestId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_SupersededByTestChangeRequestId",
                table: "test_change_reviews",
                column: "SupersededByTestChangeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_downstream_assessment_change_request_links_AssessmentId_Cha~",
                table: "downstream_assessment_change_request_links",
                columns: new[] { "AssessmentId", "ChangeRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_downstream_assessment_change_request_links_ChangeRequestId",
                table: "downstream_assessment_change_request_links",
                column: "ChangeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_downstream_change_assessments_ProjectId_ReleaseId_TargetLev~",
                table: "downstream_change_assessments",
                columns: new[] { "ProjectId", "ReleaseId", "TargetLevel", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_downstream_change_assessments_SourceChangeRequestId_TargetL~",
                table: "downstream_change_assessments",
                columns: new[] { "SourceChangeRequestId", "TargetLevel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_downstream_change_assessments_SupersededByAssessmentId",
                table: "downstream_change_assessments",
                column: "SupersededByAssessmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_test_change_reviews_test_change_reviews_SupersededByTestCha~",
                table: "test_change_reviews",
                column: "SupersededByTestChangeRequestId",
                principalTable: "test_change_reviews",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_test_change_reviews_test_change_reviews_SupersededByTestCha~",
                table: "test_change_reviews");

            migrationBuilder.DropTable(
                name: "downstream_assessment_change_request_links");

            migrationBuilder.DropTable(
                name: "downstream_change_assessments");

            migrationBuilder.DropIndex(
                name: "IX_test_change_reviews_SupersededByTestChangeRequestId",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "SelectedApproverId",
                table: "test_procedure_revisions");

            migrationBuilder.DropColumn(
                name: "SelectedApproverId",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "SupersededByTestChangeRequestId",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "SupersededReason",
                table: "test_change_reviews");
        }
    }
}
