using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameChangeRequestCodeNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_change_impact_dispositions_system_change_requests_ScrId",
                table: "change_impact_dispositions");

            migrationBuilder.DropForeignKey(
                name: "FK_requirement_changes_system_change_requests_ScrId",
                table: "requirement_changes");

            migrationBuilder.DropForeignKey(
                name: "FK_requirement_revisions_system_change_requests_SourceScrId",
                table: "requirement_revisions");

            migrationBuilder.DropForeignKey(
                name: "FK_review_cycles_system_change_requests_ScrId",
                table: "review_cycles");

            // Renamed, not dropped and recreated. EF scaffolds a DropTable/CreateTable pair whenever a table
            // name changes, and applying that as written would have deleted every baseline selection — the
            // 107 rows recording exactly which change-request revisions each frozen baseline was built from,
            // which is the evidence a baseline exists to provide.
            migrationBuilder.RenameTable(
                name: "baseline_scr_selections",
                newName: "baseline_change_request_selections");

            migrationBuilder.RenameColumn(
                name: "ScrId",
                table: "baseline_change_request_selections",
                newName: "ChangeRequestId");

            migrationBuilder.RenameColumn(
                name: "ScrDisplayNumber",
                table: "baseline_change_request_selections",
                newName: "ChangeRequestDisplayNumber");

            migrationBuilder.RenameIndex(
                name: "IX_baseline_scr_selections_BaselineId_ScrId",
                table: "baseline_change_request_selections",
                newName: "IX_baseline_change_request_selections_BaselineId_ChangeRequest~");

            migrationBuilder.RenameColumn(
                name: "ScrId",
                table: "review_cycles",
                newName: "ChangeRequestId");

            migrationBuilder.RenameIndex(
                name: "IX_review_cycles_ScrId_Sequence",
                table: "review_cycles",
                newName: "IX_review_cycles_ChangeRequestId_Sequence");

            migrationBuilder.RenameColumn(
                name: "SourceScrId",
                table: "requirement_revisions",
                newName: "SourceChangeRequestId");

            migrationBuilder.RenameIndex(
                name: "IX_requirement_revisions_SourceScrId",
                table: "requirement_revisions",
                newName: "IX_requirement_revisions_SourceChangeRequestId");

            migrationBuilder.RenameColumn(
                name: "CreatedScrId",
                table: "requirement_interchange_jobs",
                newName: "CreatedChangeRequestId");

            migrationBuilder.RenameColumn(
                name: "ScrId",
                table: "requirement_changes",
                newName: "ChangeRequestId");

            migrationBuilder.RenameIndex(
                name: "IX_requirement_changes_ScrId_BaseNumber_Revision",
                table: "requirement_changes",
                newName: "IX_requirement_changes_ChangeRequestId_BaseNumber_Revision");

            migrationBuilder.RenameColumn(
                name: "CreatedScrId",
                table: "reqif_exchange_jobs",
                newName: "CreatedChangeRequestId");

            migrationBuilder.RenameColumn(
                name: "ScrId",
                table: "change_impact_dispositions",
                newName: "ChangeRequestId");

            migrationBuilder.RenameIndex(
                name: "IX_change_impact_dispositions_ScrId",
                table: "change_impact_dispositions",
                newName: "IX_change_impact_dispositions_ChangeRequestId");

            migrationBuilder.RenameIndex(
                name: "IX_change_impact_dispositions_CampaignId_ScrId_Kind_ArtifactRe~",
                table: "change_impact_dispositions",
                newName: "IX_change_impact_dispositions_CampaignId_ChangeRequestId_Kind_~");

            migrationBuilder.AddForeignKey(
                name: "FK_change_impact_dispositions_system_change_requests_ChangeReq~",
                table: "change_impact_dispositions",
                column: "ChangeRequestId",
                principalTable: "system_change_requests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_requirement_changes_system_change_requests_ChangeRequestId",
                table: "requirement_changes",
                column: "ChangeRequestId",
                principalTable: "system_change_requests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_requirement_revisions_system_change_requests_SourceChangeRe~",
                table: "requirement_revisions",
                column: "SourceChangeRequestId",
                principalTable: "system_change_requests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_review_cycles_system_change_requests_ChangeRequestId",
                table: "review_cycles",
                column: "ChangeRequestId",
                principalTable: "system_change_requests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        /// <summary>
        /// Deliberately empty. The forward migration only renames a table, six columns and their indexes;
        /// EF's scaffolded reversal drops and recreates the selections table, which would destroy the rows
        /// recording which change-request revisions each frozen baseline was built from. Reverting a name is
        /// not worth risking that, and rolling back is a restore from backup.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder) { }
    }
}
