using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PredecessorReleaseId",
                table: "software_releases",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE software_releases AS successor
                SET "PredecessorReleaseId" = (
                    SELECT predecessor."Id"
                    FROM software_releases AS predecessor
                    WHERE predecessor."ProjectId" = successor."ProjectId"
                      AND predecessor."IsReleased" = TRUE
                      AND predecessor."Id" <> successor."Id"
                    ORDER BY predecessor."ReleasedAt" DESC NULLS LAST, predecessor."Version" DESC
                    LIMIT 1)
                WHERE successor."IsReleased" = FALSE
                  AND successor."PredecessorReleaseId" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_software_releases_PredecessorReleaseId",
                table: "software_releases",
                column: "PredecessorReleaseId");

            migrationBuilder.AddForeignKey(
                name: "FK_software_releases_software_releases_PredecessorReleaseId",
                table: "software_releases",
                column: "PredecessorReleaseId",
                principalTable: "software_releases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_software_releases_software_releases_PredecessorReleaseId",
                table: "software_releases");

            migrationBuilder.DropIndex(
                name: "IX_software_releases_PredecessorReleaseId",
                table: "software_releases");

            migrationBuilder.DropColumn(
                name: "PredecessorReleaseId",
                table: "software_releases");
        }
    }
}
