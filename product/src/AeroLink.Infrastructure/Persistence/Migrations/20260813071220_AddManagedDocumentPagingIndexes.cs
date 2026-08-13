using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedDocumentPagingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("CREATE INDEX IX_managed_documents_search_number ON managed_documents USING gin (lower(\"DocumentNumber\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_managed_documents_search_acronym ON managed_documents USING gin (lower(\"Acronym\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_managed_documents_search_title ON managed_documents USING gin (lower(\"Title\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_managed_documents_search_type ON managed_documents USING gin (lower(\"DocumentType\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_system_change_requests_search_number ON system_change_requests USING gin (lower(\"BaseNumber\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_system_change_requests_search_title ON system_change_requests USING gin (lower(\"Title\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_problem_reports_search_number ON problem_reports USING gin (lower(\"ReportNumber\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_problem_reports_search_title ON problem_reports USING gin (lower(\"Title\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_test_change_reviews_search_number ON test_change_reviews USING gin (lower(\"BaseNumber\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_test_change_reviews_search_source ON test_change_reviews USING gin (lower(\"SourceChangeRequestNumber\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_test_change_reviews_search_problem ON test_change_reviews USING gin (lower(\"SourceProblemReportNumber\") gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX IX_releases_search_version ON software_releases USING gin (lower(\"Version\") gin_trgm_ops);");

            migrationBuilder.CreateIndex(
                name: "IX_managed_documents_ProjectId_DocumentType_DocumentNumber",
                table: "managed_documents",
                columns: new[] { "ProjectId", "DocumentType", "DocumentNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_documents_ProjectId_StewardId_DocumentNumber",
                table: "managed_documents",
                columns: new[] { "ProjectId", "StewardId", "DocumentNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_documents_ProjectId_UpdatedAt_DocumentNumber",
                table: "managed_documents",
                columns: new[] { "ProjectId", "UpdatedAt", "DocumentNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_review_steps_ApproverId_State_AssignedAt",
                table: "managed_document_review_steps",
                columns: new[] { "ApproverId", "State", "AssignedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_revisions_ResponsibleOwnerId_State_DocumentId",
                table: "managed_document_revisions",
                columns: new[] { "ResponsibleOwnerId", "State", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_links_RevisionId_CreatedAt",
                table: "managed_document_links",
                columns: new[] { "RevisionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_check_ins_RevisionId_OccurredAt",
                table: "managed_document_check_ins",
                columns: new[] { "RevisionId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_controlled_attachments_RevisionId_LogicalId_Version",
                table: "controlled_attachments",
                columns: new[] { "RevisionId", "LogicalId", "Version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_managed_documents_search_number;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_managed_documents_search_acronym;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_managed_documents_search_title;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_managed_documents_search_type;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_system_change_requests_search_number;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_system_change_requests_search_title;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_problem_reports_search_number;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_problem_reports_search_title;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_test_change_reviews_search_number;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_test_change_reviews_search_source;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_test_change_reviews_search_problem;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_releases_search_version;");

            migrationBuilder.DropIndex(
                name: "IX_managed_documents_ProjectId_DocumentType_DocumentNumber",
                table: "managed_documents");

            migrationBuilder.DropIndex(
                name: "IX_managed_documents_ProjectId_StewardId_DocumentNumber",
                table: "managed_documents");

            migrationBuilder.DropIndex(
                name: "IX_managed_documents_ProjectId_UpdatedAt_DocumentNumber",
                table: "managed_documents");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_review_steps_ApproverId_State_AssignedAt",
                table: "managed_document_review_steps");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_revisions_ResponsibleOwnerId_State_DocumentId",
                table: "managed_document_revisions");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_links_RevisionId_CreatedAt",
                table: "managed_document_links");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_check_ins_RevisionId_OccurredAt",
                table: "managed_document_check_ins");

            migrationBuilder.DropIndex(
                name: "IX_controlled_attachments_RevisionId_LogicalId_Version",
                table: "controlled_attachments");
        }
    }
}
