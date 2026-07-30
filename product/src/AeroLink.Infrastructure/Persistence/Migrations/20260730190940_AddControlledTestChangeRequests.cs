using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddControlledTestChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseNumber",
                table: "test_change_reviews",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "test_change_reviews",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "test_change_request_claims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TestChangeReviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeRequestNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ClaimedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_change_request_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_change_request_claims_test_change_reviews_TestChangeRe~",
                        column: x => x.TestChangeReviewId,
                        principalTable: "test_change_reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_test_change_request_claims_ChangeRequestId",
                table: "test_change_request_claims",
                column: "ChangeRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_change_request_claims_TestChangeReviewId",
                table: "test_change_request_claims",
                column: "TestChangeReviewId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "test_change_request_claims");

            migrationBuilder.DropColumn(
                name: "BaseNumber",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "test_change_reviews");
        }
    }
}
