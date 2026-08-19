using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArtifactClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artifact_claims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_claims", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_claims_ChangeRequestId",
                table: "artifact_claims",
                column: "ChangeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_artifact_claims_ProjectId_ArtifactKey",
                table: "artifact_claims",
                columns: new[] { "ProjectId", "ArtifactKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifact_claims");
        }
    }
}
