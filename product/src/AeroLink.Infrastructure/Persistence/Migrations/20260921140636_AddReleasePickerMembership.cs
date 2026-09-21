using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleasePickerMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PickerInsertionOrdinal",
                table: "software_releases",
                type: "bigint",
                nullable: true);

            // Release link-options membership: a database-owned insertion ordinal allocated only inside the
            // shared per-project advisory fence, plus immutability and supplied-value rejection. Existing rows
            // keep NULL as the documented legacy cohort ("present at upgrade"); no default allocates before
            // the fence and no controlled column is rewritten.
            migrationBuilder.Sql(@"
                CREATE SEQUENCE aerolink_release_picker_ordinal_seq
                    START WITH 1 INCREMENT BY 1 CACHE 1 NO CYCLE;

                CREATE OR REPLACE FUNCTION aerolink_release_picker_alloc() RETURNS trigger AS $$
                BEGIN
                    IF NEW.""PickerInsertionOrdinal"" IS NOT NULL THEN
                        RAISE EXCEPTION 'picker insertion ordinal is database-allocated and must not be supplied';
                    END IF;
                    PERFORM pg_advisory_xact_lock(hashtext('aerolink-release-picker:' || NEW.""ProjectId""::text));
                    NEW.""PickerInsertionOrdinal"" := nextval('aerolink_release_picker_ordinal_seq');
                    RETURN NEW;
                END; $$ LANGUAGE plpgsql;

                CREATE TRIGGER aerolink_release_picker_alloc_ins BEFORE INSERT ON software_releases
                    FOR EACH ROW EXECUTE FUNCTION aerolink_release_picker_alloc();

                CREATE OR REPLACE FUNCTION aerolink_release_picker_immutable() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'picker insertion membership is immutable';
                END; $$ LANGUAGE plpgsql;

                CREATE TRIGGER aerolink_release_picker_immutable_upd BEFORE UPDATE ON software_releases
                    FOR EACH ROW WHEN (NEW.""PickerInsertionOrdinal"" IS DISTINCT FROM OLD.""PickerInsertionOrdinal"")
                    EXECUTE FUNCTION aerolink_release_picker_immutable();
                ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS aerolink_release_picker_immutable_upd ON software_releases;
                DROP TRIGGER IF EXISTS aerolink_release_picker_alloc_ins ON software_releases;
                DROP FUNCTION IF EXISTS aerolink_release_picker_immutable();
                DROP FUNCTION IF EXISTS aerolink_release_picker_alloc();
                DROP SEQUENCE IF EXISTS aerolink_release_picker_ordinal_seq;
                ");

            migrationBuilder.DropColumn(
                name: "PickerInsertionOrdinal",
                table: "software_releases");
        }
    }
}
