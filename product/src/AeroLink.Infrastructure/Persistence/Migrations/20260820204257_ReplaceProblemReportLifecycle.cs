using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceProblemReportLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keep legacy rows readable while the domain/API expose only the canonical eight states.
            migrationBuilder.Sql("""
                UPDATE problem_reports
                SET "State" = CASE "State"
                    WHEN 'Investigating' THEN 'Implementing'
                    WHEN 'ResolutionProposed' THEN 'Verifying'
                    WHEN 'AwaitingSqaClosure' THEN 'WaitingForSqaToClose'
                    WHEN 'AwaitingClosureApproval' THEN 'WaitingForSqaToClose'
                    WHEN 'Deferred' THEN 'Open'
                    WHEN 'Duplicate' THEN 'Rejected'
                    WHEN 'CannotReproduce' THEN 'Rejected'
                    WHEN 'NoFaultFound' THEN 'Rejected'
                    WHEN 'AcceptedRisk' THEN 'Rejected'
                    ELSE "State"
                END
                WHERE "State" IN ('Investigating', 'ResolutionProposed', 'AwaitingSqaClosure',
                    'AwaitingClosureApproval', 'Deferred', 'Duplicate', 'CannotReproduce',
                    'NoFaultFound', 'AcceptedRisk');
                """);

            migrationBuilder.AddColumn<string>(
                name: "FromState",
                table: "problem_report_revisions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Rationale",
                table: "problem_report_revisions",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ToState",
                table: "problem_report_revisions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE problem_reports
                SET "State" = 'AwaitingSqaClosure'
                WHERE "State" = 'WaitingForSqaToClose';
                """);

            migrationBuilder.DropColumn(
                name: "FromState",
                table: "problem_report_revisions");

            migrationBuilder.DropColumn(
                name: "Rationale",
                table: "problem_report_revisions");

            migrationBuilder.DropColumn(
                name: "ToState",
                table: "problem_report_revisions");
        }
    }
}
