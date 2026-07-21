using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUniversalControlledArtifactConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "test_procedures",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "test_procedures",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "requirement_trace_links",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "requirement_trace_links",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "requirement_specifications",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "requirement_specifications",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "candidate_baselines",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "candidate_baselines",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.Sql("UPDATE test_procedures SET \"UpdatedAt\" = \"CreatedAt\";");
            migrationBuilder.Sql("UPDATE requirement_trace_links SET \"UpdatedAt\" = \"CreatedAt\";");
            migrationBuilder.Sql("UPDATE requirement_specifications SET \"UpdatedAt\" = \"CreatedAt\";");
            migrationBuilder.Sql("UPDATE candidate_baselines SET \"UpdatedAt\" = \"CreatedAt\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "test_procedures");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "test_procedures");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "requirement_trace_links");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "requirement_trace_links");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "requirement_specifications");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "requirement_specifications");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "candidate_baselines");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "candidate_baselines");
        }
    }
}
