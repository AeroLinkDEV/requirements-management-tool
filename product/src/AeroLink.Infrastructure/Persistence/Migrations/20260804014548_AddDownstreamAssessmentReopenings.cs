using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDownstreamAssessmentReopenings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecidedAt",
                table: "downstream_change_assessments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecidedBy",
                table: "downstream_change_assessments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "downstream_assessment_reopenings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PreviousOutcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PreviousRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    PreviousDecidedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PreviousDecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PreviousApprovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PreviousApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DetachedChangeRequestNumbers = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_downstream_assessment_reopenings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_downstream_assessment_reopenings_downstream_change_assessme~",
                        column: x => x.AssessmentId,
                        principalTable: "downstream_change_assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_downstream_assessment_reopenings_AssessmentId",
                table: "downstream_assessment_reopenings",
                column: "AssessmentId");

            // Existing conclusions have an author even though the column did not exist to hold it: the
            // aggregate has always refused a conclusion from anybody but the assigned engineer, so for a
            // concluded assessment the assignee IS the decider. That is derivation, not a guess. DecidedAt is
            // deliberately left null — the recorded instant was never stored and UpdatedAt would only be a
            // plausible-looking invention.
            migrationBuilder.Sql("""
                UPDATE downstream_change_assessments
                SET "DecidedBy" = "AssignedEngineerId"
                WHERE "Outcome" <> 'Pending' AND "AssignedEngineerId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "downstream_assessment_reopenings");

            migrationBuilder.DropColumn(
                name: "DecidedAt",
                table: "downstream_change_assessments");

            migrationBuilder.DropColumn(
                name: "DecidedBy",
                table: "downstream_change_assessments");
        }
    }
}
