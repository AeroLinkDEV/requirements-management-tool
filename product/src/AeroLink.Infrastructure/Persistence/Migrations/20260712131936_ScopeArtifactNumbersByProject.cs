using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeArtifactNumbersByProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_system_change_requests_BaseNumber_Revision",
                table: "system_change_requests");

            migrationBuilder.DropIndex(
                name: "IX_candidate_baselines_BaseNumber_Revision",
                table: "candidate_baselines");

            migrationBuilder.CreateIndex(
                name: "IX_system_change_requests_ProjectId_BaseNumber_Revision",
                table: "system_change_requests",
                columns: new[] { "ProjectId", "BaseNumber", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_candidate_baselines_ProjectId_BaseNumber_Revision",
                table: "candidate_baselines",
                columns: new[] { "ProjectId", "BaseNumber", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_system_change_requests_ProjectId_BaseNumber_Revision",
                table: "system_change_requests");

            migrationBuilder.DropIndex(
                name: "IX_candidate_baselines_ProjectId_BaseNumber_Revision",
                table: "candidate_baselines");

            migrationBuilder.CreateIndex(
                name: "IX_system_change_requests_BaseNumber_Revision",
                table: "system_change_requests",
                columns: new[] { "BaseNumber", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_candidate_baselines_BaseNumber_Revision",
                table: "candidate_baselines",
                columns: new[] { "BaseNumber", "Revision" },
                unique: true);
        }
    }
}
