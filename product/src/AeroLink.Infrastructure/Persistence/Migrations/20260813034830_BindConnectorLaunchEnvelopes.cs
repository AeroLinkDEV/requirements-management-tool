using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindConnectorLaunchEnvelopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeploymentId",
                table: "document_connector_grants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentNumber",
                table: "document_connector_grants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyId",
                table: "document_connector_grants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "document_connector_grants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevisionNumber",
                table: "document_connector_grants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceAttachmentId",
                table: "document_connector_grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSha256",
                table: "document_connector_grants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceSize",
                table: "document_connector_grants",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_connector_grants_DeploymentId_SourceAttachmentId",
                table: "document_connector_grants",
                columns: new[] { "DeploymentId", "SourceAttachmentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_document_connector_grants_DeploymentId_SourceAttachmentId",
                table: "document_connector_grants");

            migrationBuilder.DropColumn(
                name: "DeploymentId",
                table: "document_connector_grants");

            migrationBuilder.DropColumn(
                name: "DocumentNumber",
                table: "document_connector_grants");

            migrationBuilder.DropColumn(
                name: "KeyId",
                table: "document_connector_grants");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "document_connector_grants");

            migrationBuilder.DropColumn(
                name: "RevisionNumber",
                table: "document_connector_grants");

            migrationBuilder.DropColumn(
                name: "SourceAttachmentId",
                table: "document_connector_grants");

            migrationBuilder.DropColumn(
                name: "SourceSha256",
                table: "document_connector_grants");

            migrationBuilder.DropColumn(
                name: "SourceSize",
                table: "document_connector_grants");
        }
    }
}
