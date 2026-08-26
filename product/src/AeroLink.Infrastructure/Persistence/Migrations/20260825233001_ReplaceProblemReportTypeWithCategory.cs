using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Retires the four-kind Problem Report Type in favour of the nine-category vocabulary.
    ///
    /// The scaffolded version dropped Type first and added Category afterwards, which would have thrown away
    /// the only input the backfill has. The order here is deliberate and load-bearing: add, backfill, drop.
    ///
    /// The mapping asserts more than the retired vocabulary could hold. "Code" becomes CodeFunctional —
    /// functional impact — and "Test" becomes TestBlocking, and nobody ever made either judgement, because
    /// the four kinds had no way to express them. Every backfilled row is therefore stamped
    /// MigrationDerived so the record can say the value was assigned rather than chosen, and the first
    /// person to open the report and pick a category flips it to Selected for good.
    ///
    /// Raw SQL, and invisible to the API suite: those tests run on SQLite with EnsureCreated, so migrations
    /// are never exercised by them. This is qualified against PostgreSQL separately.
    /// </summary>
    public partial class ReplaceProblemReportTypeWithCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "problem_reports",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CategoryProvenance",
                table: "problem_reports",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            // Mirrors ProblemReportCategoryVocabulary.FromRetiredKind, which is where the mapping is stated
            // once and tested. A kind this does not name — including a row left empty by an older schema —
            // lands on TaskDriver, the one category that claims nothing about a defect.
            migrationBuilder.Sql(@"
                UPDATE problem_reports
                SET ""Category"" = CASE ""Type""
                        WHEN 'Documentation' THEN 'RequirementsDocumentation'
                        WHEN 'Code' THEN 'CodeFunctional'
                        WHEN 'Test' THEN 'TestBlocking'
                        ELSE 'TaskDriver'
                    END,
                    ""CategoryProvenance"" = 'MigrationDerived';
            ");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "problem_reports");

            migrationBuilder.CreateIndex(
                name: "IX_problem_reports_ProjectId_Category",
                table: "problem_reports",
                columns: new[] { "ProjectId", "Category" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_problem_reports_ProjectId_Category",
                table: "problem_reports");

            // Down is lossy and cannot be otherwise: nine categories collapse back onto four kinds, so a
            // report that said "Code Issue — Non-Functional Impact" comes back saying only "Code". The
            // column is added nullable, filled, and only then made NOT NULL — adding it NOT NULL with an
            // empty default, as the scaffold did, would leave every row carrying a value the retired enum
            // could not parse.
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "problem_reports",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE problem_reports
                SET ""Type"" = CASE ""Category""
                        WHEN 'RequirementsDocumentation' THEN 'Documentation'
                        WHEN 'CodeFunctional' THEN 'Code'
                        WHEN 'CodeNonFunctional' THEN 'Code'
                        WHEN 'TestBlocking' THEN 'Test'
                        WHEN 'TestNonBlocking' THEN 'Test'
                        ELSE 'Other'
                    END;
            ");

            migrationBuilder.Sql(@"ALTER TABLE problem_reports ALTER COLUMN ""Type"" SET NOT NULL;");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "CategoryProvenance",
                table: "problem_reports");
        }
    }
}
