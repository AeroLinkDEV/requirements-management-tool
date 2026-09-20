using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CaptureCodeMergeResultIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MergeResultKind",
                table: "code_evidence_contributions",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeResultSha",
                table: "code_evidence_contributions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MergedAt",
                table: "code_evidence_contributions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProviderObservedAt",
                table: "code_evidence_contributions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MergeResultKind",
                table: "code_evidence_contributions");

            migrationBuilder.DropColumn(
                name: "MergeResultSha",
                table: "code_evidence_contributions");

            migrationBuilder.DropColumn(
                name: "MergedAt",
                table: "code_evidence_contributions");

            migrationBuilder.DropColumn(
                name: "ProviderObservedAt",
                table: "code_evidence_contributions");
        }
    }
}
