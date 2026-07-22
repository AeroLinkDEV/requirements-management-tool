using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteProductLinePublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "document_templates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "document_templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedRevision",
                table: "document_templates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "controlled_libraries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    LibraryNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
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
                    table.PrimaryKey("PK_controlled_libraries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_controlled_libraries_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_template_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    TemplateKind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Organization = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BodyJson = table.Column<string>(type: "text", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_template_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_template_revisions_document_templates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "document_templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "controlled_library_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LibraryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_controlled_library_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_controlled_library_revisions_controlled_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "controlled_libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "variant_library_reuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LibraryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    LatestUpstreamRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SynchronizationState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ApplicabilityJson = table.Column<string>(type: "text", nullable: false),
                    DecisionRationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_variant_library_reuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_variant_library_reuses_controlled_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "controlled_libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_variant_library_reuses_controlled_library_revisions_LatestU~",
                        column: x => x.LatestUpstreamRevisionId,
                        principalTable: "controlled_library_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_variant_library_reuses_controlled_library_revisions_Selecte~",
                        column: x => x.SelectedRevisionId,
                        principalTable: "controlled_library_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_variant_library_reuses_product_variants_VariantId",
                        column: x => x.VariantId,
                        principalTable: "product_variants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "library_propagation_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReuseId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LibraryId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpstreamRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_propagation_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_library_propagation_decisions_controlled_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "controlled_libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_library_propagation_decisions_controlled_library_revisions_~",
                        column: x => x.PreviousRevisionId,
                        principalTable: "controlled_library_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_library_propagation_decisions_controlled_library_revisions~1",
                        column: x => x.UpstreamRevisionId,
                        principalTable: "controlled_library_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_library_propagation_decisions_product_variants_VariantId",
                        column: x => x.VariantId,
                        principalTable: "product_variants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_library_propagation_decisions_variant_library_reuses_ReuseId",
                        column: x => x.ReuseId,
                        principalTable: "variant_library_reuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_controlled_libraries_ProjectId_LibraryNumber",
                table: "controlled_libraries",
                columns: new[] { "ProjectId", "LibraryNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_controlled_library_revisions_LibraryId_Revision",
                table: "controlled_library_revisions",
                columns: new[] { "LibraryId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_controlled_library_revisions_ManifestHash",
                table: "controlled_library_revisions",
                column: "ManifestHash");

            migrationBuilder.CreateIndex(
                name: "IX_document_template_revisions_ManifestHash",
                table: "document_template_revisions",
                column: "ManifestHash");

            migrationBuilder.CreateIndex(
                name: "IX_document_template_revisions_TemplateId_Revision",
                table: "document_template_revisions",
                columns: new[] { "TemplateId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_library_propagation_decisions_LibraryId",
                table: "library_propagation_decisions",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_library_propagation_decisions_PreviousRevisionId",
                table: "library_propagation_decisions",
                column: "PreviousRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_library_propagation_decisions_ReuseId_DecidedAt",
                table: "library_propagation_decisions",
                columns: new[] { "ReuseId", "DecidedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_library_propagation_decisions_UpstreamRevisionId",
                table: "library_propagation_decisions",
                column: "UpstreamRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_library_propagation_decisions_VariantId",
                table: "library_propagation_decisions",
                column: "VariantId");

            migrationBuilder.CreateIndex(
                name: "IX_variant_library_reuses_LatestUpstreamRevisionId",
                table: "variant_library_reuses",
                column: "LatestUpstreamRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_variant_library_reuses_LibraryId",
                table: "variant_library_reuses",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_variant_library_reuses_SelectedRevisionId",
                table: "variant_library_reuses",
                column: "SelectedRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_variant_library_reuses_VariantId_LibraryId",
                table: "variant_library_reuses",
                columns: new[] { "VariantId", "LibraryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_template_revisions");

            migrationBuilder.DropTable(
                name: "library_propagation_decisions");

            migrationBuilder.DropTable(
                name: "variant_library_reuses");

            migrationBuilder.DropTable(
                name: "controlled_library_revisions");

            migrationBuilder.DropTable(
                name: "controlled_libraries");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "ApprovedRevision",
                table: "document_templates");
        }
    }
}
