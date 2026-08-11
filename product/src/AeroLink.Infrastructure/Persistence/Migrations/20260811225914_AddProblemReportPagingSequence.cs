using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProblemReportPagingSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NumberSequence",
                table: "problem_reports",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Preserve the historical numeric-suffix ordering while retaining the legacy fallback for
            // controlled identifiers that do not end in a representable integer.
            migrationBuilder.Sql("""
                UPDATE problem_reports
                SET "NumberSequence" = CASE
                    WHEN "ReportNumber" ~ '-[0-9]+$'
                        AND length(substring("ReportNumber" from '([0-9]+)$')) <= 10
                        AND CAST(substring("ReportNumber" from '([0-9]+)$') AS bigint) <= 2147483647
                    THEN CAST(substring("ReportNumber" from '([0-9]+)$') AS integer)
                    ELSE 1
                END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_problem_reports_ProjectId_NumberSequence_Revision_Id",
                table: "problem_reports",
                columns: new[] { "ProjectId", "NumberSequence", "Revision", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_problem_reports_ProjectId_NumberSequence_Revision_Id",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "NumberSequence",
                table: "problem_reports");
        }
    }
}
