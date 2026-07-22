using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductLineConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_line_components",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComponentNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_line_components", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_line_components_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ApplicabilityJson = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_variants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_variants_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "component_streams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uuid", nullable: false),
                    StreamKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_component_streams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_component_streams_product_line_components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "product_line_components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "component_stream_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_component_stream_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_component_stream_revisions_component_streams_StreamId",
                        column: x => x.StreamId,
                        principalTable: "component_streams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "variant_component_selections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComponentRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicabilityJson = table.Column<string>(type: "text", nullable: false),
                    SelectedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_variant_component_selections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_variant_component_selections_component_stream_revisions_Com~",
                        column: x => x.ComponentRevisionId,
                        principalTable: "component_stream_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_variant_component_selections_product_variants_VariantId",
                        column: x => x.VariantId,
                        principalTable: "product_variants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_component_stream_revisions_ManifestHash",
                table: "component_stream_revisions",
                column: "ManifestHash");

            migrationBuilder.CreateIndex(
                name: "IX_component_stream_revisions_StreamId_Revision",
                table: "component_stream_revisions",
                columns: new[] { "StreamId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_component_streams_ComponentId_StreamKey",
                table: "component_streams",
                columns: new[] { "ComponentId", "StreamKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_line_components_ProjectId_ComponentNumber",
                table: "product_line_components",
                columns: new[] { "ProjectId", "ComponentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_ProjectId_VariantKey",
                table: "product_variants",
                columns: new[] { "ProjectId", "VariantKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_variant_component_selections_ComponentRevisionId",
                table: "variant_component_selections",
                column: "ComponentRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_variant_component_selections_VariantId_ComponentRevisionId",
                table: "variant_component_selections",
                columns: new[] { "VariantId", "ComponentRevisionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "variant_component_selections");

            migrationBuilder.DropTable(
                name: "component_stream_revisions");

            migrationBuilder.DropTable(
                name: "product_variants");

            migrationBuilder.DropTable(
                name: "component_streams");

            migrationBuilder.DropTable(
                name: "product_line_components");
        }
    }
}
