using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTestExecutionReleaseScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_test_executions_ProjectId_ExecutedAt",
                table: "test_executions");

            migrationBuilder.AddColumn<Guid>(
                name: "ReleaseId",
                table: "test_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE test_executions AS execution
                SET "ReleaseId" = build."ReleaseId"
                FROM software_builds AS build
                WHERE execution."SoftwareBuildId" = build."Id"
                  AND execution."ReleaseId" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_test_executions_ProjectId_ReleaseId_ExecutedAt",
                table: "test_executions",
                columns: new[] { "ProjectId", "ReleaseId", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_test_executions_ReleaseId",
                table: "test_executions",
                column: "ReleaseId");

            migrationBuilder.AddForeignKey(
                name: "FK_test_executions_software_releases_ReleaseId",
                table: "test_executions",
                column: "ReleaseId",
                principalTable: "software_releases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_test_executions_software_releases_ReleaseId",
                table: "test_executions");

            migrationBuilder.DropIndex(
                name: "IX_test_executions_ProjectId_ReleaseId_ExecutedAt",
                table: "test_executions");

            migrationBuilder.DropIndex(
                name: "IX_test_executions_ReleaseId",
                table: "test_executions");

            migrationBuilder.DropColumn(
                name: "ReleaseId",
                table: "test_executions");

            migrationBuilder.CreateIndex(
                name: "IX_test_executions_ProjectId_ExecutedAt",
                table: "test_executions",
                columns: new[] { "ProjectId", "ExecutedAt" });
        }
    }
}
