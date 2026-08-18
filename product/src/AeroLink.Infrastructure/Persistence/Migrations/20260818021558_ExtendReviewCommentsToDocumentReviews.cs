using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtendReviewCommentsToDocumentReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ReviewCycleId",
                table: "review_comments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "DocumentCycle",
                table: "review_comments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ManagedDocumentRevisionId",
                table: "review_comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SectionLabel",
                table: "review_comments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_review_comments_ManagedDocumentRevisionId_DocumentCycle_Sta~",
                table: "review_comments",
                columns: new[] { "ManagedDocumentRevisionId", "DocumentCycle", "State" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_review_comments_document_cycle",
                table: "review_comments",
                sql: "(\"ManagedDocumentRevisionId\" IS NULL) = (\"DocumentCycle\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_review_comments_one_owner",
                table: "review_comments",
                sql: "(\"ReviewCycleId\" IS NULL) <> (\"ManagedDocumentRevisionId\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_review_comments_managed_document_revisions_ManagedDocumentR~",
                table: "review_comments",
                column: "ManagedDocumentRevisionId",
                principalTable: "managed_document_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_review_comments_managed_document_revisions_ManagedDocumentR~",
                table: "review_comments");

            migrationBuilder.DropIndex(
                name: "IX_review_comments_ManagedDocumentRevisionId_DocumentCycle_Sta~",
                table: "review_comments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_review_comments_document_cycle",
                table: "review_comments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_review_comments_one_owner",
                table: "review_comments");

            migrationBuilder.DropColumn(
                name: "DocumentCycle",
                table: "review_comments");

            migrationBuilder.DropColumn(
                name: "ManagedDocumentRevisionId",
                table: "review_comments");

            migrationBuilder.DropColumn(
                name: "SectionLabel",
                table: "review_comments");

            migrationBuilder.AlterColumn<Guid>(
                name: "ReviewCycleId",
                table: "review_comments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
