using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteProblemReportMvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdditionalInformation",
                table: "problem_reports",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AdditionalInformationRich",
                table: "problem_reports",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ImpactAssessmentJson",
                table: "problem_reports",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProblemRich",
                table: "problem_reports",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ResponsibleEngineerId",
                table: "problem_reports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SystemAircraftImpact",
                table: "problem_reports",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "TargetReleaseId",
                table: "problem_reports",
                type: "uuid",
                nullable: true);

            // Preserve every existing controlled PR while moving it onto the agreed MVP vocabulary.
            migrationBuilder.Sql("UPDATE problem_reports SET \"ResponsibleEngineerId\" = \"ReportedBy\", \"ImpactAssessmentJson\" = '{}' WHERE \"ResponsibleEngineerId\" = '';");
            migrationBuilder.Sql("UPDATE problem_reports SET \"State\" = 'Implementing' WHERE \"State\" = 'Investigating';");
            migrationBuilder.Sql("UPDATE problem_reports SET \"State\" = 'Verifying' WHERE \"State\" = 'ResolutionProposed';");
            migrationBuilder.Sql("UPDATE problem_reports SET \"State\" = 'AwaitingSqaClosure' WHERE \"State\" = 'AwaitingClosureApproval';");
            migrationBuilder.Sql("UPDATE problem_reports SET \"TargetReleaseId\" = (SELECT links.\"ArtifactId\" FROM problem_report_links links WHERE links.\"ProblemReportId\" = problem_reports.\"Id\" AND links.\"ArtifactType\" = 'Release' AND links.\"Relationship\" = 'BuildScope' LIMIT 1) WHERE \"TargetReleaseId\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdditionalInformation",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "AdditionalInformationRich",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "ImpactAssessmentJson",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "ProblemRich",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "ResponsibleEngineerId",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "SystemAircraftImpact",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "TargetReleaseId",
                table: "problem_reports");
        }
    }
}
