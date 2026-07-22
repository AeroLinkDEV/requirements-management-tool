using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResumableReqIfCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempt",
                table: "reqif_exchange_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CheckpointJson",
                table: "reqif_exchange_jobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "reqif_exchange_jobs",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProcessedCount",
                table: "reqif_exchange_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attempt",
                table: "reqif_exchange_jobs");

            migrationBuilder.DropColumn(
                name: "CheckpointJson",
                table: "reqif_exchange_jobs");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "reqif_exchange_jobs");

            migrationBuilder.DropColumn(
                name: "ProcessedCount",
                table: "reqif_exchange_jobs");
        }
    }
}
