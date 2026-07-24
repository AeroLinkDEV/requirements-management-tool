using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations;

public partial class AddOidcAuthenticationAttempts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name:"oidc_authentication_attempts",
            columns:table=>new
            {
                id=table.Column<Guid>(nullable:false),
                provider_id=table.Column<Guid>(nullable:false),
                state_hash=table.Column<string>(maxLength:64,nullable:false),
                nonce_hash=table.Column<string>(maxLength:64,nullable:false),
                code_verifier_protected=table.Column<string>(maxLength:2000,nullable:false),
                return_path=table.Column<string>(maxLength:500,nullable:false),
                created_at=table.Column<DateTimeOffset>(nullable:false),
                expires_at=table.Column<DateTimeOffset>(nullable:false),
                consumed_at=table.Column<DateTimeOffset>(nullable:true)
            },
            constraints:table=>
            {
                table.PrimaryKey("pk_oidc_authentication_attempts",x=>x.id);
                table.ForeignKey(name:"fk_oidc_authentication_attempts_provider",column:x=>x.provider_id,principalTable:"external_identity_providers",principalColumn:"id",onDelete:ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name:"ux_oidc_authentication_attempts_state",table:"oidc_authentication_attempts",column:"state_hash",unique:true);
        migrationBuilder.CreateIndex(name:"ix_oidc_authentication_attempts_expiry",table:"oidc_authentication_attempts",columns:new[]{"expires_at","consumed_at"});
        migrationBuilder.CreateIndex(name:"ix_oidc_authentication_attempts_provider",table:"oidc_authentication_attempts",column:"provider_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.DropTable(name:"oidc_authentication_attempts");
}
