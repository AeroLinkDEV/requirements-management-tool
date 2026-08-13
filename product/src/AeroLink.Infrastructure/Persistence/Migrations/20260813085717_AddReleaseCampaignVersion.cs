using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseCampaignVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "release_campaigns",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Existing campaigns have a real CreatedAt but no recorded update instant. Treat their creation
            // as the last known change rather than leaving a year-one sentinel in a controlled record.
            migrationBuilder.Sql("""
                UPDATE "release_campaigns" SET "UpdatedAt" = "CreatedAt";
                """);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "release_campaigns",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.Sql("""
                UPDATE "release_campaigns" SET "Version" = 1 WHERE "Version" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "release_campaigns");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "release_campaigns");
        }
    }
}
