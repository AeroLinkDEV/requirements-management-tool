using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KeyTestChangeRequestExclusivityByRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_test_change_reviews_ChangeRequestId_Discipline",
                table: "test_change_reviews");

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_ChangeRequestId_Discipline_Revision",
                table: "test_change_reviews",
                columns: new[] { "ChangeRequestId", "Discipline", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_test_change_reviews_ChangeRequestId_Discipline_Revision",
                table: "test_change_reviews");

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_ChangeRequestId_Discipline",
                table: "test_change_reviews",
                columns: new[] { "ChangeRequestId", "Discipline" },
                unique: true);
        }
    }
}
