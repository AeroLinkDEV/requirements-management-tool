using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewStageKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "review_workflow_stages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Review");

            migrationBuilder.AddColumn<string>(
                name: "StageKind",
                table: "approval_steps",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Review");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "review_workflow_stages");

            migrationBuilder.DropColumn(
                name: "StageKind",
                table: "approval_steps");
        }
    }
}
