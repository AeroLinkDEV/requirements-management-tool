using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeRequestTargetReleaseProjectBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_software_releases_ProjectId_Id",
                table: "software_releases",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_system_change_requests_ProjectId_TargetReleaseId",
                table: "system_change_requests",
                columns: new[] { "ProjectId", "TargetReleaseId" });

            migrationBuilder.AddForeignKey(
                name: "FK_system_change_requests_software_releases_ProjectId_TargetRe~",
                table: "system_change_requests",
                columns: new[] { "ProjectId", "TargetReleaseId" },
                principalTable: "software_releases",
                principalColumns: new[] { "ProjectId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_system_change_requests_software_releases_ProjectId_TargetRe~",
                table: "system_change_requests");

            migrationBuilder.DropIndex(
                name: "IX_system_change_requests_ProjectId_TargetReleaseId",
                table: "system_change_requests");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_software_releases_ProjectId_Id",
                table: "software_releases");
        }
    }
}
