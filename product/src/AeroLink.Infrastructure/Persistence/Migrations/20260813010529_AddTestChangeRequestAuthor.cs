using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTestChangeRequestAuthor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorId",
                table: "test_change_reviews",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // Existing packages take the engineer assigned to them, where one was. That is the closest true
            // answer available: nobody recorded who raised them, and the assigned engineer is the person who
            // owns the work. Packages with no assignment keep the empty author and read as raised by the
            // assessment that created them — which is what actually happened to most of them.
            migrationBuilder.Sql("""
                UPDATE test_change_reviews
                SET "AuthorId" = "AssignedEngineerId"
                WHERE "AssignedEngineerId" IS NOT NULL AND "AssignedEngineerId" <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorId",
                table: "test_change_reviews");
        }
    }
}
