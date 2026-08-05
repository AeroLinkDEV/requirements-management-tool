using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProblemReportTypeAndWorkaround : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Other" rather than the scaffolded empty string. The kind is stored by name and "" is not a
            // name, so every existing report would fail to materialize. It is also the honest answer: no
            // report written before this column existed was ever classified.
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "problem_reports",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Other");

            migrationBuilder.AddColumn<string>(
                name: "Workaround",
                table: "problem_reports",
                type: "text",
                nullable: false,
                defaultValue: "");

            // The impact assessment's "Safety" area becomes "Airworthiness", which is what is actually being
            // judged. Answers already recorded move with the name instead of being stranded under a key
            // nothing reads any more. Only the key can match — the values are Unknown, No and Yes.
            migrationBuilder.Sql("""
                UPDATE problem_reports
                SET "ImpactAssessmentJson" = replace("ImpactAssessmentJson", '"Safety"', '"Airworthiness"')
                WHERE "ImpactAssessmentJson" LIKE '%"Safety"%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE problem_reports
                SET "ImpactAssessmentJson" = replace("ImpactAssessmentJson", '"Airworthiness"', '"Safety"')
                WHERE "ImpactAssessmentJson" LIKE '%"Airworthiness"%';
                """);

            migrationBuilder.DropColumn(
                name: "Type",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "Workaround",
                table: "problem_reports");
        }
    }
}
