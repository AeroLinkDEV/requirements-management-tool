using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShareReviewCyclesWithTestChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ChangeRequestId",
                table: "review_cycles",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "TestChangeReviewId",
                table: "review_cycles",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_cycles_TestChangeReviewId_Sequence",
                table: "review_cycles",
                columns: new[] { "TestChangeReviewId", "Sequence" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_review_cycles_one_owner",
                table: "review_cycles",
                sql: "(\"ChangeRequestId\" IS NULL) <> (\"TestChangeReviewId\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_review_cycles_test_change_reviews_TestChangeReviewId",
                table: "review_cycles",
                column: "TestChangeReviewId",
                principalTable: "test_change_reviews",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_review_cycles_test_change_reviews_TestChangeReviewId",
                table: "review_cycles");

            migrationBuilder.DropIndex(
                name: "IX_review_cycles_TestChangeReviewId_Sequence",
                table: "review_cycles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_review_cycles_one_owner",
                table: "review_cycles");

            migrationBuilder.DropColumn(
                name: "TestChangeReviewId",
                table: "review_cycles");

            migrationBuilder.AlterColumn<Guid>(
                name: "ChangeRequestId",
                table: "review_cycles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
