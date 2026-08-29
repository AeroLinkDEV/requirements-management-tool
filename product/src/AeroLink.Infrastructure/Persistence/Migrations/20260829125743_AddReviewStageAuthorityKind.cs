using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewStageAuthorityKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequiredAuthorityKind",
                table: "review_workflow_stages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiredAuthorityKind",
                table: "review_workflow_stages");
        }
    }
}
