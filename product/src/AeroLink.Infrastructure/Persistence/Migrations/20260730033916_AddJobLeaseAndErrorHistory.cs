using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobLeaseAndErrorHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAt",
                table: "enterprise_operation_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                table: "enterprise_operation_jobs",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorHistoryJson",
                table: "enterprise_operation_jobs",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "enterprise_operation_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "enterprise_operation_jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_enterprise_operation_jobs_LeaseExpiresAt",
                table: "enterprise_operation_jobs",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_enterprise_operation_jobs_State_CreatedAt",
                table: "enterprise_operation_jobs",
                columns: new[] { "State", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_enterprise_operation_jobs_LeaseExpiresAt",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropIndex(
                name: "IX_enterprise_operation_jobs_State_CreatedAt",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "ErrorHistoryJson",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "enterprise_operation_jobs");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "enterprise_operation_jobs");
        }
    }
}
