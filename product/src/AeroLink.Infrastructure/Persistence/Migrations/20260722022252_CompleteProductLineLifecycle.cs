using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteProductLineLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BaseRevisionId",
                table: "configuration_change_sets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                table: "configuration_change_sets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ComponentId",
                table: "configuration_change_sets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConflictJson",
                table: "configuration_change_sets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeResultJson",
                table: "configuration_change_sets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionRationale",
                table: "configuration_change_sets",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceRevisionId",
                table: "configuration_change_sets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetRevisionId",
                table: "configuration_change_sets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetStreamId",
                table: "configuration_change_sets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "component_propagation_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComponentRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_component_propagation_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_component_propagation_decisions_component_stream_revisions_~",
                        column: x => x.ComponentRevisionId,
                        principalTable: "component_stream_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_component_propagation_decisions_product_variants_VariantId",
                        column: x => x.VariantId,
                        principalTable: "product_variants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_variant_baselines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_variant_baselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_variant_baselines_product_variants_VariantId",
                        column: x => x.VariantId,
                        principalTable: "product_variants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_configuration_change_sets_ComponentId_State",
                table: "configuration_change_sets",
                columns: new[] { "ComponentId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_component_propagation_decisions_ComponentRevisionId",
                table: "component_propagation_decisions",
                column: "ComponentRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_component_propagation_decisions_VariantId_ComponentRevision~",
                table: "component_propagation_decisions",
                columns: new[] { "VariantId", "ComponentRevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_variant_baselines_ManifestHash",
                table: "product_variant_baselines",
                column: "ManifestHash");

            migrationBuilder.CreateIndex(
                name: "IX_product_variant_baselines_VariantId_Revision",
                table: "product_variant_baselines",
                columns: new[] { "VariantId", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "component_propagation_decisions");

            migrationBuilder.DropTable(
                name: "product_variant_baselines");

            migrationBuilder.DropIndex(
                name: "IX_configuration_change_sets_ComponentId_State",
                table: "configuration_change_sets");

            migrationBuilder.DropColumn(
                name: "BaseRevisionId",
                table: "configuration_change_sets");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "configuration_change_sets");

            migrationBuilder.DropColumn(
                name: "ComponentId",
                table: "configuration_change_sets");

            migrationBuilder.DropColumn(
                name: "ConflictJson",
                table: "configuration_change_sets");

            migrationBuilder.DropColumn(
                name: "MergeResultJson",
                table: "configuration_change_sets");

            migrationBuilder.DropColumn(
                name: "ResolutionRationale",
                table: "configuration_change_sets");

            migrationBuilder.DropColumn(
                name: "SourceRevisionId",
                table: "configuration_change_sets");

            migrationBuilder.DropColumn(
                name: "TargetRevisionId",
                table: "configuration_change_sets");

            migrationBuilder.DropColumn(
                name: "TargetStreamId",
                table: "configuration_change_sets");
        }
    }
}
