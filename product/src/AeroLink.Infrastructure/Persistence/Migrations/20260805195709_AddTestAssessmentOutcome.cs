using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTestAssessmentOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecidedAt",
                table: "test_change_reviews",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecidedBy",
                table: "test_change_reviews",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NoChangeRationale",
                table: "test_change_reviews",
                type: "text",
                nullable: false,
                defaultValue: "");

            // "Pending" rather than the scaffolded empty string: the outcome is stored by name and "" is not
            // a name, so every existing review would fail to materialize.
            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "test_change_reviews",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Pending");

            // A row that already carries a controlled number was raised under the old rule, which numbered
            // every approved change on sight. Its number is nonetheless a standing assertion that test work
            // is required, so it is recorded as having concluded that rather than being asked again. Rows
            // without a number were never assessed, and Pending is the truthful answer for them.
            migrationBuilder.Sql("""
                UPDATE test_change_reviews
                SET "Outcome" = 'ChangeRequired'
                WHERE "BaseNumber" <> ''
                   OR EXISTS (SELECT 1 FROM verification_impact_items i WHERE i."TestChangeReviewId" = test_change_reviews."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DecidedAt",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "DecidedBy",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "NoChangeRationale",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "test_change_reviews");
        }
    }
}
