using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFrozenReviewTraceAdjacency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "frozen_review_trace_links",
                columns: table => new
                {
                    CycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_frozen_review_trace_links", x => new { x.CycleId, x.UpstreamId });
                    table.ForeignKey(
                        name: "FK_frozen_review_trace_links_review_cycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "review_cycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_frozen_review_trace_links_system_change_requests_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_frozen_review_trace_links_OwnerId",
                table: "frozen_review_trace_links",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_frozen_review_trace_links_ProjectId_OwnerId",
                table: "frozen_review_trace_links",
                columns: new[] { "ProjectId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_frozen_review_trace_links_ProjectId_UpstreamId",
                table: "frozen_review_trace_links",
                columns: new[] { "ProjectId", "UpstreamId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "frozen_review_trace_links");
        }
    }
}
