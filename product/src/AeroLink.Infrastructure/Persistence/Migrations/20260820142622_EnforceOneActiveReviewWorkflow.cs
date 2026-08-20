using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceOneActiveReviewWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_review_workflows_ProjectId_AppliesTo_State",
                table: "review_workflows");

            migrationBuilder.CreateIndex(
                name: "IX_review_workflows_ProjectId_AppliesTo_State",
                table: "review_workflows",
                columns: new[] { "ProjectId", "AppliesTo", "State" },
                unique: true,
                filter: "\"State\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_review_workflows_ProjectId_AppliesTo_State",
                table: "review_workflows");

            migrationBuilder.CreateIndex(
                name: "IX_review_workflows_ProjectId_AppliesTo_State",
                table: "review_workflows",
                columns: new[] { "ProjectId", "AppliesTo", "State" });
        }
    }
}
