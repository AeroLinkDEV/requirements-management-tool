using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedDocumentReviewRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "managed_document_review_steps",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Review");

            // Every historical managed-document route was created by the former fixed two-slot API.
            // Its last position was explicitly the release-authorizing SQA/configuration approver;
            // preceding positions were content reviews. Preserve that known meaning without
            // manufacturing any other historical workflow evidence.
            migrationBuilder.Sql(
                """
                UPDATE managed_document_review_steps AS step
                SET "Kind" = 'Approval'
                WHERE step."Position" = (
                    SELECT MAX(other."Position")
                    FROM managed_document_review_steps AS other
                    WHERE other."RevisionId" = step."RevisionId"
                      AND other."Cycle" = step."Cycle"
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "managed_document_review_steps");
        }
    }
}
