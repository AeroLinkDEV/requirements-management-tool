using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ControlProblemReportReleaseWaivers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalAuthority",
                table: "readiness_waivers",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovedByAccountId",
                table: "readiness_waivers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BlockerRevision",
                table: "readiness_waivers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "BlockerVersion",
                table: "readiness_waivers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "readiness_waivers",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RevocationReason",
                table: "readiness_waivers",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RevokedBy",
                table: "readiness_waivers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SignatureMeaning",
                table: "readiness_waivers",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "ReleaseBlockerVersion",
                table: "problem_reports",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Existing generic waivers were client-attributed and existing PR waiver strings were never
            // independently approved. Preserve both as history, but never promote either into valid release
            // evidence. Existing blockers receive a context marker only so the next authorized waiver can
            // bind to the retained controlled version.
            migrationBuilder.Sql("""
                UPDATE readiness_waivers
                SET "Provenance" = 'LegacyClientAttributed'
                WHERE "Provenance" = '';

                UPDATE problem_reports
                SET "ReleaseBlockerVersion" = "Version"
                WHERE "IsReleaseBlocker" = TRUE AND "ReleaseBlockerVersion" = 0;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_readiness_waivers_ProjectId_BlockerType_BlockerId_BlockerRe~",
                table: "readiness_waivers",
                columns: new[] { "ProjectId", "BlockerType", "BlockerId", "BlockerRevision", "BlockerVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_readiness_waivers_ProjectId_BlockerType_BlockerId_BlockerRe~",
                table: "readiness_waivers");

            migrationBuilder.DropColumn(
                name: "ApprovalAuthority",
                table: "readiness_waivers");

            migrationBuilder.DropColumn(
                name: "ApprovedByAccountId",
                table: "readiness_waivers");

            migrationBuilder.DropColumn(
                name: "BlockerRevision",
                table: "readiness_waivers");

            migrationBuilder.DropColumn(
                name: "BlockerVersion",
                table: "readiness_waivers");

            migrationBuilder.DropColumn(
                name: "Provenance",
                table: "readiness_waivers");

            migrationBuilder.DropColumn(
                name: "RevocationReason",
                table: "readiness_waivers");

            migrationBuilder.DropColumn(
                name: "RevokedBy",
                table: "readiness_waivers");

            migrationBuilder.DropColumn(
                name: "SignatureMeaning",
                table: "readiness_waivers");

            migrationBuilder.DropColumn(
                name: "ReleaseBlockerVersion",
                table: "problem_reports");
        }
    }
}
