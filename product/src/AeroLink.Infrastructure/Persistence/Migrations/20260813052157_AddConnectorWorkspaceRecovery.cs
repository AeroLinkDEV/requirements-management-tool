using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorWorkspaceRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RecoveryWorkspaceId",
                table: "document_connector_grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_connector_grants_RecoveryWorkspaceId",
                table: "document_connector_grants",
                column: "RecoveryWorkspaceId",
                unique: true,
                filter: "\"RevokedAt\" IS NULL AND \"RecoveryWorkspaceId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_document_connector_grants_RecoveryWorkspaceId",
                table: "document_connector_grants");

            migrationBuilder.DropColumn(
                name: "RecoveryWorkspaceId",
                table: "document_connector_grants");
        }
    }
}
