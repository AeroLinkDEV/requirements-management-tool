using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowProblemReportOriginatedTestChangeRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ChangeRequestId",
                table: "test_change_reviews",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "OriginatingProblemReportId",
                table: "test_change_reviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceProblemReportNumber",
                table: "test_change_reviews",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_test_change_reviews_OriginatingProblemReportId",
                table: "test_change_reviews",
                column: "OriginatingProblemReportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_test_change_reviews_OriginatingProblemReportId",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "OriginatingProblemReportId",
                table: "test_change_reviews");

            migrationBuilder.DropColumn(
                name: "SourceProblemReportNumber",
                table: "test_change_reviews");

            migrationBuilder.AlterColumn<Guid>(
                name: "ChangeRequestId",
                table: "test_change_reviews",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
