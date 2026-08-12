using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeparateManagedDocumentStewardship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "managed_documents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StewardId",
                table: "managed_documents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "InitiatedBy",
                table: "managed_document_revisions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ResponsibleOwnerId",
                table: "managed_document_revisions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // The legacy OwnerId values are retained verbatim and become the best
            // available initial stewardship/responsibility evidence. Creation actors
            // were not stored separately, so mark them from the same retained value;
            // later assignments are append-only and do not rewrite this backfill.
            migrationBuilder.Sql(
                """
                UPDATE managed_documents
                SET "StewardId" = "OwnerId", "CreatedBy" = "OwnerId";
                UPDATE managed_document_revisions
                SET "ResponsibleOwnerId" = "OwnerId", "InitiatedBy" = "OwnerId";
                """);

            migrationBuilder.CreateTable(
                name: "managed_document_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignmentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PriorAssigneeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NewAssigneeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AssignedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_document_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_document_assignments_managed_document_revisions_Rev~",
                        column: x => x.RevisionId,
                        principalTable: "managed_document_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_document_assignments_managed_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "managed_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_document_review_contributors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewCycle = table.Column<int>(type: "integer", nullable: false),
                    ContributorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Provenance = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_document_review_contributors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_document_review_contributors_managed_document_revis~",
                        column: x => x.RevisionId,
                        principalTable: "managed_document_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Historical schemas did not freeze a contributor set per submission.
            // Recover only attributable check-in actors for the latest retained cycle
            // and label the inference so it cannot masquerade as contemporary proof.
            migrationBuilder.Sql(
                """
                INSERT INTO managed_document_review_contributors
                    ("Id", "RevisionId", "ReviewCycle", "ContributorId", "EvidenceHash", "CapturedAt", "Provenance")
                SELECT md5(check_in."RevisionId"::text || ':' || cycle."ReviewCycle"::text || ':' || check_in."ActorId")::uuid,
                       check_in."RevisionId", cycle."ReviewCycle", check_in."ActorId",
                       (array_agg(check_in."ResultSha256" ORDER BY check_in."WorkingVersion" DESC))[1],
                       revision."SubmittedAt", 'LegacyInferredFromRetainedCheckIns'
                FROM managed_document_check_ins check_in
                JOIN managed_document_revisions revision ON revision."Id" = check_in."RevisionId"
                JOIN (SELECT "RevisionId", max("Cycle") AS "ReviewCycle" FROM managed_document_review_steps GROUP BY "RevisionId") cycle
                  ON cycle."RevisionId" = check_in."RevisionId"
                WHERE revision."SubmittedAt" IS NOT NULL
                GROUP BY check_in."RevisionId", cycle."ReviewCycle", check_in."ActorId", revision."SubmittedAt";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_assignments_DocumentId_EffectiveAt",
                table: "managed_document_assignments",
                columns: new[] { "DocumentId", "EffectiveAt" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_assignments_RevisionId",
                table: "managed_document_assignments",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_review_contributors_RevisionId_ReviewCycle~",
                table: "managed_document_review_contributors",
                columns: new[] { "RevisionId", "ReviewCycle", "ContributorId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "managed_document_assignments");

            migrationBuilder.DropTable(
                name: "managed_document_review_contributors");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "managed_documents");

            migrationBuilder.DropColumn(
                name: "StewardId",
                table: "managed_documents");

            migrationBuilder.DropColumn(
                name: "InitiatedBy",
                table: "managed_document_revisions");

            migrationBuilder.DropColumn(
                name: "ResponsibleOwnerId",
                table: "managed_document_revisions");
        }
    }
}
