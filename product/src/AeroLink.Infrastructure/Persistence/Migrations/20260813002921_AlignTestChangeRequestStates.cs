using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlignTestChangeRequestStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeferralReason",
                table: "test_change_reviews",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DeferredFromState",
                table: "test_change_reviews",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            // The state is stored as its name, so renaming the enum value renames the data. Without this every
            // existing package holds a string the enum no longer has and fails to materialize on read — the
            // whole verification side would 500 on a database that has ever created a test change request.
            //
            // Scaffolding cannot know this: EF sees an enum member renamed and emits nothing.
            migrationBuilder.Sql("""UPDATE test_change_reviews SET "State" = 'Draft' WHERE "State" = 'Open';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deferred packages have no pre-rename equivalent, so they go back as Drafts rather than as a
            // value the old enum could not read. Going down loses that they were on the shelf; going down
            // with an unreadable state loses the row.
            migrationBuilder.Sql("""UPDATE test_change_reviews SET "State" = 'Open' WHERE "State" IN ('Draft', 'Deferred');""");

            migrationBuilder.DropColumn(
                name: "DeferralReason",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "DeferredFromState",
                table: "test_change_reviews");
        }
    }
}
