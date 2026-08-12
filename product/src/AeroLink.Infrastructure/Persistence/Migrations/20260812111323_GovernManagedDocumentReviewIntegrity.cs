using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GovernManagedDocumentReviewIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AssignedAt",
                table: "managed_document_review_steps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorityPolicy",
                table: "managed_document_review_steps",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "LegacyUnspecified");

            migrationBuilder.AddColumn<string>(
                name: "AuthoritySource",
                table: "managed_document_review_steps",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "LegacyUnspecified");

            migrationBuilder.AddColumn<Guid>(
                name: "AuthoritySourceId",
                table: "managed_document_review_steps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GrantedAuthority",
                table: "managed_document_review_steps",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "LegacyUnspecified");

            migrationBuilder.AddColumn<string>(
                name: "RequiredAuthority",
                table: "managed_document_review_steps",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "LegacyUnspecified");

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "managed_document_review_steps",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowId",
                table: "managed_document_review_steps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowName",
                table: "managed_document_review_steps",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "Legacy managed-document review");

            migrationBuilder.AddColumn<int>(
                name: "WorkflowVersion",
                table: "managed_document_review_steps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "Authority",
                table: "electronic_signatures",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40);

            migrationBuilder.AddColumn<string>(
                name: "AuthoritySource",
                table: "electronic_signatures",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "AuthoritySourceId",
                table: "electronic_signatures",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Rationale",
                table: "electronic_signatures",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ReviewCycle",
                table: "electronic_signatures",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewStepId",
                table: "electronic_signatures",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewStepPosition",
                table: "electronic_signatures",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowId",
                table: "electronic_signatures",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkflowVersion",
                table: "electronic_signatures",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "managed_document_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResultJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_document_operations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_document_operations_managed_document_revisions_Revi~",
                        column: x => x.RevisionId,
                        principalTable: "managed_document_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_operations_RevisionId_OperationType_Operat~",
                table: "managed_document_operations",
                columns: new[] { "RevisionId", "OperationType", "OperationKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "managed_document_operations");

            migrationBuilder.DropColumn(
                name: "AssignedAt",
                table: "managed_document_review_steps");

            migrationBuilder.DropColumn(
                name: "AuthorityPolicy",
                table: "managed_document_review_steps");

            migrationBuilder.DropColumn(
                name: "AuthoritySource",
                table: "managed_document_review_steps");

            migrationBuilder.DropColumn(
                name: "AuthoritySourceId",
                table: "managed_document_review_steps");

            migrationBuilder.DropColumn(
                name: "GrantedAuthority",
                table: "managed_document_review_steps");

            migrationBuilder.DropColumn(
                name: "RequiredAuthority",
                table: "managed_document_review_steps");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "managed_document_review_steps");

            migrationBuilder.DropColumn(
                name: "WorkflowId",
                table: "managed_document_review_steps");

            migrationBuilder.DropColumn(
                name: "WorkflowName",
                table: "managed_document_review_steps");

            migrationBuilder.DropColumn(
                name: "WorkflowVersion",
                table: "managed_document_review_steps");

            migrationBuilder.DropColumn(
                name: "AuthoritySource",
                table: "electronic_signatures");

            migrationBuilder.DropColumn(
                name: "AuthoritySourceId",
                table: "electronic_signatures");

            migrationBuilder.DropColumn(
                name: "Rationale",
                table: "electronic_signatures");

            migrationBuilder.DropColumn(
                name: "ReviewCycle",
                table: "electronic_signatures");

            migrationBuilder.DropColumn(
                name: "ReviewStepId",
                table: "electronic_signatures");

            migrationBuilder.DropColumn(
                name: "ReviewStepPosition",
                table: "electronic_signatures");

            migrationBuilder.DropColumn(
                name: "WorkflowId",
                table: "electronic_signatures");

            migrationBuilder.DropColumn(
                name: "WorkflowVersion",
                table: "electronic_signatures");

            migrationBuilder.AlterColumn<string>(
                name: "Authority",
                table: "electronic_signatures",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);
        }
    }
}
