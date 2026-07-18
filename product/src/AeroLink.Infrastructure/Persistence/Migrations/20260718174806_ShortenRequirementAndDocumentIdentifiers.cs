using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShortenRequirementAndDocumentIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("Npgsql"))
            {
                foreach (var (table, column) in ControlledIdentifierColumns())
                    migrationBuilder.Sql("UPDATE \"" + table + "\" SET \"" + column + "\" = regexp_replace(\"" + column + "\", '-00([0-9]{6})(\\.[0-9]{2})?$', '-\\1\\2') WHERE \"" + column + "\" ~ '-00[0-9]{6}(\\.[0-9]{2})?$';");
            }
            else if (migrationBuilder.ActiveProvider.Contains("Sqlite"))
            {
                foreach (var (table, column) in ControlledIdentifierColumns())
                    migrationBuilder.Sql("UPDATE \"" + table + "\" SET \"" + column + "\" = substr(\"" + column + "\", 1, instr(\"" + column + "\", '-')) || substr(\"" + column + "\", instr(\"" + column + "\", '-') + 3) WHERE instr(\"" + column + "\", '-') > 0 AND substr(\"" + column + "\", instr(\"" + column + "\", '-') + 1, 2) = '00' AND (length(\"" + column + "\") - instr(\"" + column + "\", '-') = 8 OR length(\"" + column + "\") - instr(\"" + column + "\", '-') = 11);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("Npgsql"))
            {
                foreach (var (table, column) in ControlledIdentifierColumns())
                    migrationBuilder.Sql("UPDATE \"" + table + "\" SET \"" + column + "\" = regexp_replace(\"" + column + "\", '-([0-9]{6})(\\.[0-9]{2})?$', '-00\\1\\2') WHERE \"" + column + "\" ~ '-[0-9]{6}(\\.[0-9]{2})?$';");
            }
            else if (migrationBuilder.ActiveProvider.Contains("Sqlite"))
            {
                foreach (var (table, column) in ControlledIdentifierColumns())
                    migrationBuilder.Sql("UPDATE \"" + table + "\" SET \"" + column + "\" = substr(\"" + column + "\", 1, instr(\"" + column + "\", '-')) || '00' || substr(\"" + column + "\", instr(\"" + column + "\", '-') + 1) WHERE instr(\"" + column + "\", '-') > 0 AND (length(\"" + column + "\") - instr(\"" + column + "\", '-') = 6 OR length(\"" + column + "\") - instr(\"" + column + "\", '-') = 9);");
            }
        }

        private static (string Table, string Column)[] ControlledIdentifierColumns() =>
        [
            ("requirements", "BaseNumber"),
            ("requirement_changes", "BaseNumber"),
            ("test_procedures", "BaseNumber"),
            ("controlled_documents", "DocumentNumber"),
            ("requirement_specifications", "DocumentNumber")
        ];
    }
}
