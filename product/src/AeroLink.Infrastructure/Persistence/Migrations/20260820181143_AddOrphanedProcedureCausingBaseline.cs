using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrphanedProcedureCausingBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CausingBaselineId",
                table: "verification_impact_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_items_CausingBaselineId",
                table: "verification_impact_items",
                column: "CausingBaselineId");

            migrationBuilder.AddForeignKey(
                name: "FK_verification_impact_items_candidate_baselines_CausingBaseli~",
                table: "verification_impact_items",
                column: "CausingBaselineId",
                principalTable: "candidate_baselines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_verification_impact_items_candidate_baselines_CausingBaseli~",
                table: "verification_impact_items");

            migrationBuilder.DropIndex(
                name: "IX_verification_impact_items_CausingBaselineId",
                table: "verification_impact_items");

            migrationBuilder.DropColumn(
                name: "CausingBaselineId",
                table: "verification_impact_items");
        }
    }
}
