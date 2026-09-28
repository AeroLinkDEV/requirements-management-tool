using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntegrityProblemReportSourcePackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integrity_import_batches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreviewHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MappingJson = table.Column<string>(type: "text", nullable: false),
                    ReconciliationJson = table.Column<string>(type: "text", nullable: false),
                    ReceiptJson = table.Column<string>(type: "text", nullable: false),
                    PackageAttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrity_import_batches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_integrity_import_batches_controlled_attachments_PackageAtta~",
                        column: x => x.PackageAttachmentId,
                        principalTable: "controlled_attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_integrity_import_batches_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "integrity_report_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(19)", maxLength: 19, nullable: false),
                    ItemPath = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    DateJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrity_report_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_integrity_report_sources_integrity_import_batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "integrity_import_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_integrity_report_sources_problem_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "problem_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_integrity_report_sources_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_integrity_import_batches_PackageAttachmentId",
                table: "integrity_import_batches",
                column: "PackageAttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_integrity_import_batches_ProjectId_ActorId_OperationId",
                table: "integrity_import_batches",
                columns: new[] { "ProjectId", "ActorId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integrity_report_sources_BatchId",
                table: "integrity_report_sources",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_integrity_report_sources_ProjectId_SourceInstanceId_SourceK~",
                table: "integrity_report_sources",
                columns: new[] { "ProjectId", "SourceInstanceId", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integrity_report_sources_ReportId",
                table: "integrity_report_sources",
                column: "ReportId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integrity_report_sources");

            migrationBuilder.DropTable(
                name: "integrity_import_batches");
        }
    }
}
