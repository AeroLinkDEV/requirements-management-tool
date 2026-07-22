using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQualityIntelligenceEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certification_evidence_index",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectiveCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClaimBoundary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IndexedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IndexedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certification_evidence_index", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quality_lifecycle_objectives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TargetJson = table.Column<string>(type: "text", nullable: false),
                    EvidenceExpectation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quality_lifecycle_objectives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "readiness_waivers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockerType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    BlockerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_readiness_waivers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_certification_evidence_index_ProjectId_ObjectiveCode_Artifa~",
                table: "certification_evidence_index",
                columns: new[] { "ProjectId", "ObjectiveCode", "ArtifactType", "ArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quality_lifecycle_objectives_ProjectId_Code",
                table: "quality_lifecycle_objectives",
                columns: new[] { "ProjectId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_readiness_waivers_ProjectId_BlockerType_BlockerId_ExpiresAt",
                table: "readiness_waivers",
                columns: new[] { "ProjectId", "BlockerType", "BlockerId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "certification_evidence_index");

            migrationBuilder.DropTable(
                name: "quality_lifecycle_objectives");

            migrationBuilder.DropTable(
                name: "readiness_waivers");
        }
    }
}
