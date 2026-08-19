using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOriginReleaseId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OriginReleaseId",
                table: "system_change_requests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every existing change request was raised in the build it currently targets: nothing has moved
            // yet, because until now there was nowhere to record that it had. Without this the column is all
            // zeroes and every existing record disappears from its own build listing.
            // Double-quoted identifiers are read the same way by PostgreSQL and SQLite, so one statement serves
            // the product database and the test databases alike.
            migrationBuilder.Sql(
                "UPDATE \"system_change_requests\" SET \"OriginReleaseId\" = \"TargetReleaseId\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginReleaseId",
                table: "system_change_requests");
        }
    }
}
