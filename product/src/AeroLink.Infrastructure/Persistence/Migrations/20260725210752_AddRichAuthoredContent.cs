using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRichAuthoredContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnalysisRich",
                table: "system_change_requests",
                type: "character varying(200000)",
                maxLength: 200000,
                nullable: false,
                defaultValue: "{\"blocks\":[]}");

            migrationBuilder.AddColumn<string>(
                name: "ProblemRich",
                table: "system_change_requests",
                type: "character varying(200000)",
                maxLength: 200000,
                nullable: false,
                defaultValue: "{\"blocks\":[]}");

            migrationBuilder.AddColumn<string>(
                name: "SolutionRich",
                table: "system_change_requests",
                type: "character varying(200000)",
                maxLength: 200000,
                nullable: false,
                defaultValue: "{\"blocks\":[]}");

            migrationBuilder.AlterColumn<string>(
                name: "RichText",
                table: "requirement_changes",
                type: "character varying(200000)",
                maxLength: 200000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32000)",
                oldMaxLength: 32000);

            // Every existing change case becomes its own authored content, as the single paragraph it
            // already was. Leaving these empty would make an approved change request read as having no
            // problem statement the first time somebody opened it in the new editor.
            var empty = "'" + "{\"blocks\":[]}" + "'";
            foreach (var column in new[] { "Problem", "Analysis", "Solution" })
                migrationBuilder.Sql(
                    $"UPDATE system_change_requests SET \"{column}Rich\" = json_build_object('blocks', " +
                    $"json_build_array(json_build_object('type', 'paragraph', 'text', \"{column}\")))::text " +
                    $"WHERE \"{column}\" <> '' AND (\"{column}Rich\" = '' OR \"{column}Rich\" = {empty});");

            // Supporting content stored before this model existed is plain text. It is read as a paragraph
            // on the way out, so it needs no rewrite here; converting it would rewrite rows that are
            // referenced by recorded review snapshot hashes.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnalysisRich",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "ProblemRich",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "SolutionRich",
                table: "system_change_requests");

            migrationBuilder.AlterColumn<string>(
                name: "RichText",
                table: "requirement_changes",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200000)",
                oldMaxLength: 200000);
        }
    }
}
