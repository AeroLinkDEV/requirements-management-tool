using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations;

public partial class AddExternalIdentityAccountBindings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "external_identity_account_bindings",
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false),
                provider_id = table.Column<Guid>(nullable: false),
                external_subject = table.Column<string>(maxLength: 500, nullable: false),
                user_id = table.Column<Guid>(nullable: false),
                created_by = table.Column<string>(maxLength: 100, nullable: false),
                created_at = table.Column<DateTimeOffset>(nullable: false),
                last_authenticated_at = table.Column<DateTimeOffset>(nullable: true),
                disabled_at = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_external_identity_account_bindings", x => x.id);
                table.ForeignKey(name: "fk_external_identity_account_bindings_provider", column: x => x.provider_id, principalTable: "external_identity_providers", principalColumn: "id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey(name: "fk_external_identity_account_bindings_user", column: x => x.user_id, principalTable: "user_accounts", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(name: "ux_external_identity_account_bindings_subject", table: "external_identity_account_bindings", columns: new[] { "provider_id", "external_subject" }, unique: true);
        migrationBuilder.CreateIndex(name: "ux_external_identity_account_bindings_user", table: "external_identity_account_bindings", columns: new[] { "provider_id", "user_id" }, unique: true);
        migrationBuilder.CreateIndex(name: "ix_external_identity_account_bindings_user_id", table: "external_identity_account_bindings", column: "user_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "external_identity_account_bindings");
}
