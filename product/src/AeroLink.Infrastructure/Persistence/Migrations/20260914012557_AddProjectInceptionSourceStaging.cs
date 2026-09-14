using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectInceptionSourceStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_requirement_revisions_origin_xor",
                table: "requirement_revisions");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceBaselineId",
                table: "requirement_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InceptionBaselineId",
                table: "project_setup_drafts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AlterColumn<string>(
                name: "SourceSystemVersion",
                table: "baseline_imports",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(60)",
                oldMaxLength: 60);

            migrationBuilder.AlterColumn<string>(
                name: "SourceBaselineName",
                table: "baseline_imports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "SourceBaselineDate",
                table: "baseline_imports",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<string>(
                name: "ExtractedBy",
                table: "baseline_imports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ExtractedAt",
                table: "baseline_imports",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.CreateTable(
                name: "project_setup_source_packages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceBaselineId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    FileName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    SourceTool = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: false),
                    AnalysisJson = table.Column<string>(type: "text", nullable: false),
                    SelectedCategoriesJson = table.Column<string>(type: "text", nullable: false),
                    MappingJson = table.Column<string>(type: "text", nullable: false),
                    ReconciliationJson = table.Column<string>(type: "text", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Stage = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CapturedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    MaterializedProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    MaterializedBaselineId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssertionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_setup_source_packages", x => x.Id);
                    table.CheckConstraint("CK_project_setup_source_package_size", "((\"Kind\" = 'AeroLinkBaseline' AND \"SizeBytes\" = 0) OR (\"Kind\" = 'ExternalBaseline' AND \"SizeBytes\" > 0))");
                    table.ForeignKey(
                        name: "FK_project_setup_source_packages_project_setup_drafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "project_setup_drafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_inception_source_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    SourceModule = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceIdentifier = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceRevision = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SourceState = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceSnapshotJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_inception_source_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_inception_source_records_candidate_baselines_Baseli~",
                        column: x => x.BaselineId,
                        principalTable: "candidate_baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_inception_source_records_project_setup_source_packa~",
                        column: x => x.PackageId,
                        principalTable: "project_setup_source_packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_inception_source_records_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_requirement_revisions_SourceBaselineId",
                table: "requirement_revisions",
                column: "SourceBaselineId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_requirement_revisions_origin_xor",
                table: "requirement_revisions",
                sql: "((\"OriginKind\" = 'ChangeRequest' AND \"SourceChangeRequestId\" IS NOT NULL AND \"SourceBaselineImportId\" IS NULL AND \"SourceBaselineId\" IS NULL) OR (\"OriginKind\" = 'ExternalSourcePackage' AND \"SourceChangeRequestId\" IS NULL AND \"SourceBaselineImportId\" IS NOT NULL AND \"SourceBaselineId\" IS NULL) OR (\"OriginKind\" = 'InheritedAeroLinkBaseline' AND \"SourceChangeRequestId\" IS NULL AND \"SourceBaselineImportId\" IS NULL AND \"SourceBaselineId\" IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_drafts_InceptionBaselineId",
                table: "project_setup_drafts",
                column: "InceptionBaselineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_inception_source_records_BaselineId",
                table: "project_inception_source_records",
                column: "BaselineId");

            migrationBuilder.CreateIndex(
                name: "IX_project_inception_source_records_PackageId_SourceKey_Target~",
                table: "project_inception_source_records",
                columns: new[] { "PackageId", "SourceKey", "TargetKind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_inception_source_records_ProjectId_TargetKind_Targe~",
                table: "project_inception_source_records",
                columns: new[] { "ProjectId", "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_source_packages_DraftId_Sha256",
                table: "project_setup_source_packages",
                columns: new[] { "DraftId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_source_packages_MaterializedProjectId",
                table: "project_setup_source_packages",
                column: "MaterializedProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_requirement_revisions_candidate_baselines_SourceBaselineId",
                table: "requirement_revisions",
                column: "SourceBaselineId",
                principalTable: "candidate_baselines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_requirement_revisions_candidate_baselines_SourceBaselineId",
                table: "requirement_revisions");

            migrationBuilder.DropTable(
                name: "project_inception_source_records");

            migrationBuilder.DropTable(
                name: "project_setup_source_packages");

            migrationBuilder.DropIndex(
                name: "IX_requirement_revisions_SourceBaselineId",
                table: "requirement_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_requirement_revisions_origin_xor",
                table: "requirement_revisions");

            migrationBuilder.DropIndex(
                name: "IX_project_setup_drafts_InceptionBaselineId",
                table: "project_setup_drafts");

            migrationBuilder.DropColumn(
                name: "SourceBaselineId",
                table: "requirement_revisions");

            migrationBuilder.DropColumn(
                name: "InceptionBaselineId",
                table: "project_setup_drafts");

            migrationBuilder.AlterColumn<string>(
                name: "SourceSystemVersion",
                table: "baseline_imports",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(60)",
                oldMaxLength: 60,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SourceBaselineName",
                table: "baseline_imports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "SourceBaselineDate",
                table: "baseline_imports",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ExtractedBy",
                table: "baseline_imports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ExtractedAt",
                table: "baseline_imports",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_requirement_revisions_origin_xor",
                table: "requirement_revisions",
                sql: "((\"OriginKind\" = 'ChangeRequest' AND \"SourceChangeRequestId\" IS NOT NULL AND \"SourceBaselineImportId\" IS NULL) OR (\"OriginKind\" = 'ExternalSourcePackage' AND \"SourceChangeRequestId\" IS NULL AND \"SourceBaselineImportId\" IS NOT NULL))");
        }
    }
}
