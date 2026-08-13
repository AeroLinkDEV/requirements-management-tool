using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseApprovalCycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_release_approvals_CampaignId_Position",
                table: "release_approvals");

            migrationBuilder.AddColumn<int>(
                name: "Cycle",
                table: "release_approvals",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_release_approvals_CampaignId_Cycle_Position",
                table: "release_approvals",
                columns: new[] { "CampaignId", "Cycle", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_release_approvals_CampaignId_Cycle_Position",
                table: "release_approvals");

            migrationBuilder.DropColumn(
                name: "Cycle",
                table: "release_approvals");

            migrationBuilder.CreateIndex(
                name: "IX_release_approvals_CampaignId_Position",
                table: "release_approvals",
                columns: new[] { "CampaignId", "Position" },
                unique: true);
        }
    }
}
