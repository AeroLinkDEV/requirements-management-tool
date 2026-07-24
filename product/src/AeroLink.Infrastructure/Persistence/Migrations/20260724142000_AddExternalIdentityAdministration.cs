using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations;

public partial class AddExternalIdentityAdministration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "external_identity_providers",
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false),
                key = table.Column<string>(maxLength: 100, nullable: false),
                display_name = table.Column<string>(maxLength: 200, nullable: false),
                protocol = table.Column<string>(maxLength: 30, nullable: false),
                issuer = table.Column<string>(maxLength: 1000, nullable: false),
                subject_claim = table.Column<string>(maxLength: 100, nullable: false),
                group_claim = table.Column<string>(maxLength: 100, nullable: false),
                enabled = table.Column<bool>(nullable: false),
                created_by = table.Column<string>(maxLength: 100, nullable: false),
                created_at = table.Column<DateTimeOffset>(nullable: false),
                disabled_at = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table => table.PrimaryKey("pk_external_identity_providers", x => x.id));
        migrationBuilder.CreateIndex(name:"ux_external_identity_providers_key",table:"external_identity_providers",column:"key",unique:true);
        migrationBuilder.CreateIndex(name:"ux_external_identity_providers_issuer",table:"external_identity_providers",column:"issuer",unique:true);

        migrationBuilder.CreateTable(
            name: "external_group_role_mappings",
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false),
                provider_id = table.Column<Guid>(nullable: false),
                external_group = table.Column<string>(maxLength: 300, nullable: false),
                program_id = table.Column<Guid>(nullable: false),
                role = table.Column<string>(maxLength: 40, nullable: false),
                enabled = table.Column<bool>(nullable: false),
                created_by = table.Column<string>(maxLength: 100, nullable: false),
                created_at = table.Column<DateTimeOffset>(nullable: false),
                disabled_at = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_external_group_role_mappings", x => x.id);
                table.ForeignKey(name:"fk_external_group_role_mappings_provider",column:x=>x.provider_id,principalTable:"external_identity_providers",principalColumn:"id",onDelete:ReferentialAction.Restrict);
                table.ForeignKey(name:"fk_external_group_role_mappings_program",column:x=>x.program_id,principalTable:"programs",principalColumn:"Id",onDelete:ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex(name:"ix_external_group_role_mappings_provider",table:"external_group_role_mappings",column:"provider_id");
        migrationBuilder.CreateIndex(name:"ix_external_group_role_mappings_program",table:"external_group_role_mappings",column:"program_id");
        migrationBuilder.CreateIndex(name:"ux_external_group_role_mappings_authority",table:"external_group_role_mappings",columns:new[]{"provider_id","external_group","program_id","role"},unique:true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name:"external_group_role_mappings");
        migrationBuilder.DropTable(name:"external_identity_providers");
    }
}
