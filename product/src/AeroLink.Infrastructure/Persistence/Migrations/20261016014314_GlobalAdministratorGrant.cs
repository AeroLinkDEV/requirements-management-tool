using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GlobalAdministratorGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GlobalAdministratorGrantedAt",
                table: "user_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GlobalAdministratorGrantedBy",
                table: "user_accounts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GlobalAdministratorRevokedAt",
                table: "user_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GlobalAdministratorRevokedBy",
                table: "user_accounts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBuiltInAdministrator",
                table: "user_accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsGlobalAdministrator",
                table: "user_accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_user_accounts_single_built_in_administrator",
                table: "user_accounts",
                column: "IsBuiltInAdministrator",
                unique: true,
                filter: "\"IsBuiltInAdministrator\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_accounts_single_built_in_administrator",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "GlobalAdministratorGrantedAt",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "GlobalAdministratorGrantedBy",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "GlobalAdministratorRevokedAt",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "GlobalAdministratorRevokedBy",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "IsBuiltInAdministrator",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "IsGlobalAdministrator",
                table: "user_accounts");
        }
    }
}
