using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProcedureControlledDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_test_procedure_documents_ProjectId_Level",
                table: "test_procedure_documents");

            migrationBuilder.AddColumn<string>(
                name: "ArtifactKind",
                table: "test_procedure_documents",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Case");

            // Historical software documents are the Case registers created by #722; System has always been
            // Procedure-only. Backfill those meanings explicitly rather than inferring from mutable titles.
            migrationBuilder.Sql("""
                UPDATE test_procedure_documents
                SET "ArtifactKind" = 'Procedure'
                WHERE "Level" = 'System';

                ALTER TABLE test_procedure_documents
                ADD CONSTRAINT "CK_test_procedure_documents_ArtifactKind"
                CHECK ("ArtifactKind" IN ('Case', 'Procedure'));

                ALTER TABLE test_procedure_documents
                ADD CONSTRAINT "CK_test_procedure_documents_SystemProcedureOnly"
                CHECK ("Level" <> 'System' OR "ArtifactKind" = 'Procedure');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_documents_ProjectId_Level_ArtifactKind",
                table: "test_procedure_documents",
                columns: new[] { "ProjectId", "Level", "ArtifactKind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE test_procedure_documents
                DROP CONSTRAINT IF EXISTS "CK_test_procedure_documents_ArtifactKind";
                ALTER TABLE test_procedure_documents
                DROP CONSTRAINT IF EXISTS "CK_test_procedure_documents_SystemProcedureOnly";
                """);
            migrationBuilder.DropIndex(
                name: "IX_test_procedure_documents_ProjectId_Level_ArtifactKind",
                table: "test_procedure_documents");

            migrationBuilder.DropColumn(
                name: "ArtifactKind",
                table: "test_procedure_documents");

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_documents_ProjectId_Level",
                table: "test_procedure_documents",
                columns: new[] { "ProjectId", "Level" },
                unique: true);
        }
    }
}
