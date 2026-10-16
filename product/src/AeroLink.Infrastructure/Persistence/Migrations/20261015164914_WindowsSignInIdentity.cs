using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WindowsSignInIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "user_accounts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Local");

            migrationBuilder.AddColumn<string>(
                name: "WindowsPrincipal",
                table: "user_accounts",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WindowsSid",
                table: "user_accounts",
                type: "character varying(184)",
                maxLength: 184,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthenticationMethod",
                table: "security_audit_events",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WindowsPrincipal",
                table: "security_audit_events",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AuthenticationMethod",
                table: "electronic_signatures",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_user_accounts_WindowsSid",
                table: "user_accounts",
                column: "WindowsSid",
                unique: true,
                filter: "\"WindowsSid\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_accounts_WindowsSid",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "WindowsPrincipal",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "WindowsSid",
                table: "user_accounts");

            migrationBuilder.DropColumn(
                name: "AuthenticationMethod",
                table: "security_audit_events");

            migrationBuilder.DropColumn(
                name: "WindowsPrincipal",
                table: "security_audit_events");

            migrationBuilder.DropColumn(
                name: "AuthenticationMethod",
                table: "electronic_signatures");
        }
    }
}
