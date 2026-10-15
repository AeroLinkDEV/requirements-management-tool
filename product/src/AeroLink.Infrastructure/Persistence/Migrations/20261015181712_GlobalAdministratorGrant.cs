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
                name: "IsGlobalAdministrator",
                table: "user_accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // #1639: global administration was the account named `admin`. That account keeps it, now as an
            // attributable grant from the migration, and the grant is recorded in the security audit log the
            // same way an in-app grant is.
            migrationBuilder.Sql("""
                UPDATE user_accounts
                SET "IsGlobalAdministrator" = TRUE,
                    "GlobalAdministratorGrantedBy" = 'system.migration',
                    "GlobalAdministratorGrantedAt" = now()
                WHERE "UserName" = 'admin';

                INSERT INTO security_audit_events ("Id", "EventType", "ActorId", "Target", "Outcome", "Detail", "IpAddress", "OccurredAt")
                SELECT gen_random_uuid(), 'GlobalAdministratorGranted', 'system.migration', "UserName", 'Success',
                    'Global administrator carried over from the built-in admin account when administration became a persisted grant (#1639).',
                    'local', now()
                FROM user_accounts
                WHERE "UserName" = 'admin';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                name: "IsGlobalAdministrator",
                table: "user_accounts");
        }
    }
}
