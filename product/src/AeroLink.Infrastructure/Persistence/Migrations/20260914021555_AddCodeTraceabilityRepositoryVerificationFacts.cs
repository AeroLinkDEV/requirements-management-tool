using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeTraceabilityRepositoryVerificationFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RepositoryConfigurationVersion",
                table: "code_traceability_records",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RepositoryVerifiedAt",
                table: "code_traceability_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepositoryVerifiedBy",
                table: "code_traceability_records",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VerifiedRemoteProjectId",
                table: "code_traceability_records",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedRepositoryEndpoint",
                table: "code_traceability_records",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedRepositoryPath",
                table: "code_traceability_records",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepositoryConfigurationVersion",
                table: "code_traceability_records");

            migrationBuilder.DropColumn(
                name: "RepositoryVerifiedAt",
                table: "code_traceability_records");

            migrationBuilder.DropColumn(
                name: "RepositoryVerifiedBy",
                table: "code_traceability_records");

            migrationBuilder.DropColumn(
                name: "VerifiedRemoteProjectId",
                table: "code_traceability_records");

            migrationBuilder.DropColumn(
                name: "VerifiedRepositoryEndpoint",
                table: "code_traceability_records");

            migrationBuilder.DropColumn(
                name: "VerifiedRepositoryPath",
                table: "code_traceability_records");
        }
    }
}
