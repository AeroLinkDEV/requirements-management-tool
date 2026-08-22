using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowExternalRequirementOrigins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "SourceChangeRequestId",
                table: "requirement_revisions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "OriginKind",
                table: "requirement_revisions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceBaselineImportId",
                table: "requirement_revisions",
                type: "uuid",
                nullable: true);

            // Existing revisions were all created by approved change requests. Populate the discriminator before
            // adding the XOR constraint; leaving the scaffolded empty default would make a pre-feature upgrade
            // fail while a clean install would appear healthy.
            migrationBuilder.Sql("UPDATE \"requirement_revisions\" SET \"OriginKind\" = 'ChangeRequest' WHERE \"OriginKind\" = ''");

            migrationBuilder.AddColumn<Guid>(
                name: "BoundCandidateBaselineId",
                table: "baseline_imports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PackageBoundAt",
                table: "baseline_imports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageManifestHash",
                table: "baseline_imports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "baseline_external_package_selections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SelectedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baseline_external_package_selections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_baseline_external_package_selections_baseline_imports_Basel~",
                        column: x => x.BaselineImportId,
                        principalTable: "baseline_imports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_baseline_external_package_selections_candidate_baselines_Ba~",
                        column: x => x.BaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "baseline_import_package_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Statement = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SourceIdentifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StagedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_baseline_import_package_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_baseline_import_package_items_baseline_imports_BaselineImpo~",
                        column: x => x.BaselineImportId,
                        principalTable: "baseline_imports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_baseline_import_package_items_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_baseline_import_package_items_source_identities_SourceIdent~",
                        column: x => x.SourceIdentityId,
                        principalTable: "source_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_requirement_revisions_SourceBaselineImportId",
                table: "requirement_revisions",
                column: "SourceBaselineImportId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_requirement_revisions_origin_xor",
                table: "requirement_revisions",
                sql: "((\"OriginKind\" = 'ChangeRequest' AND \"SourceChangeRequestId\" IS NOT NULL AND \"SourceBaselineImportId\" IS NULL) OR (\"OriginKind\" = 'ExternalSourcePackage' AND \"SourceChangeRequestId\" IS NULL AND \"SourceBaselineImportId\" IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_baseline_imports_BoundCandidateBaselineId",
                table: "baseline_imports",
                column: "BoundCandidateBaselineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_baseline_external_package_selections_BaselineId_BaselineImp~",
                table: "baseline_external_package_selections",
                columns: new[] { "BaselineId", "BaselineImportId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_baseline_external_package_selections_BaselineImportId",
                table: "baseline_external_package_selections",
                column: "BaselineImportId");

            migrationBuilder.CreateIndex(
                name: "IX_baseline_import_package_items_BaselineImportId_SourceIdenti~",
                table: "baseline_import_package_items",
                columns: new[] { "BaselineImportId", "SourceIdentityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_baseline_import_package_items_ProjectId_BaseNumber_Revision",
                table: "baseline_import_package_items",
                columns: new[] { "ProjectId", "BaseNumber", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_baseline_import_package_items_SourceIdentityId",
                table: "baseline_import_package_items",
                column: "SourceIdentityId");

            migrationBuilder.AddForeignKey(
                name: "FK_requirement_revisions_baseline_imports_SourceBaselineImport~",
                table: "requirement_revisions",
                column: "SourceBaselineImportId",
                principalTable: "baseline_imports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_requirement_revisions_baseline_imports_SourceBaselineImport~",
                table: "requirement_revisions");

            migrationBuilder.DropTable(
                name: "baseline_external_package_selections");

            migrationBuilder.DropTable(
                name: "baseline_import_package_items");

            migrationBuilder.DropIndex(
                name: "IX_requirement_revisions_SourceBaselineImportId",
                table: "requirement_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_requirement_revisions_origin_xor",
                table: "requirement_revisions");

            migrationBuilder.DropIndex(
                name: "IX_baseline_imports_BoundCandidateBaselineId",
                table: "baseline_imports");

            migrationBuilder.DropColumn(
                name: "OriginKind",
                table: "requirement_revisions");

            migrationBuilder.DropColumn(
                name: "SourceBaselineImportId",
                table: "requirement_revisions");

            migrationBuilder.DropColumn(
                name: "BoundCandidateBaselineId",
                table: "baseline_imports");

            migrationBuilder.DropColumn(
                name: "PackageBoundAt",
                table: "baseline_imports");

            migrationBuilder.DropColumn(
                name: "PackageManifestHash",
                table: "baseline_imports");

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceChangeRequestId",
                table: "requirement_revisions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
