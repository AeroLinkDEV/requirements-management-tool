using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTestProcedureDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "test_procedure_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Level = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_procedure_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_procedure_documents_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "test_procedure_document_nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Heading = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ProcedureId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_procedure_document_nodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_procedure_document_nodes_test_procedure_documents_Docu~",
                        column: x => x.DocumentId,
                        principalTable: "test_procedure_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_test_procedure_document_nodes_test_procedures_ProcedureId",
                        column: x => x.ProcedureId,
                        principalTable: "test_procedures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_document_nodes_DocumentId_ParentId_Position",
                table: "test_procedure_document_nodes",
                columns: new[] { "DocumentId", "ParentId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_document_nodes_ProcedureId",
                table: "test_procedure_document_nodes",
                column: "ProcedureId",
                unique: true,
                filter: "\"ProcedureId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_documents_DocumentNumber",
                table: "test_procedure_documents",
                column: "DocumentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_documents_ProjectId_Level",
                table: "test_procedure_documents",
                columns: new[] { "ProjectId", "Level" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "test_procedure_document_nodes");

            migrationBuilder.DropTable(
                name: "test_procedure_documents");
        }
    }
}
