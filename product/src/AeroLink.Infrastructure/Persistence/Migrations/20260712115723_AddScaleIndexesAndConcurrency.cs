using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScaleIndexesAndConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "system_change_requests",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_system_change_requests_ProjectId_State",
                table: "system_change_requests",
                columns: new[] { "ProjectId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_system_change_requests_ProjectId_UpdatedAt",
                table: "system_change_requests",
                columns: new[] { "ProjectId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_requirement_changes_BaseNumber",
                table: "requirement_changes",
                column: "BaseNumber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_system_change_requests_ProjectId_State",
                table: "system_change_requests");

            migrationBuilder.DropIndex(
                name: "IX_system_change_requests_ProjectId_UpdatedAt",
                table: "system_change_requests");

            migrationBuilder.DropIndex(
                name: "IX_requirement_changes_BaseNumber",
                table: "requirement_changes");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "system_change_requests");
        }
    }
}
