using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetainVerificationImpactDecisionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ResolvedProcedureRevisionId",
                table: "verification_impact_items",
                type: "uuid",
                nullable: true);

            // Existing decisions named only the procedure artifact. Freeze them to the approved revision that
            // was current when this schema becomes authoritative, so future revisions cannot rewrite history.
            migrationBuilder.Sql(
                """
                UPDATE verification_impact_items AS impact
                SET "ResolvedProcedureRevisionId" = (
                    SELECT revision."Id"
                    FROM test_procedure_revisions AS revision
                    WHERE revision."ProcedureId" = impact."ResolvedProcedureId"
                      AND revision."State" = 'Approved'
                    ORDER BY revision."Revision" DESC
                    LIMIT 1
                )
                WHERE impact."ResolvedProcedureId" IS NOT NULL
                  AND impact."ResolvedProcedureRevisionId" IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "verification_impact_decision_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VerificationImpactItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ProcedureId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProcedureRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_impact_decision_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_verification_impact_decision_history_test_procedure_revisio~",
                        column: x => x.ProcedureRevisionId,
                        principalTable: "test_procedure_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_verification_impact_decision_history_test_procedures_Proced~",
                        column: x => x.ProcedureId,
                        principalTable: "test_procedures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_verification_impact_decision_history_verification_impact_it~",
                        column: x => x.VerificationImpactItemId,
                        principalTable: "verification_impact_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_items_ResolvedProcedureId",
                table: "verification_impact_items",
                column: "ResolvedProcedureId");

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_items_ResolvedProcedureRevisionId",
                table: "verification_impact_items",
                column: "ResolvedProcedureRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_decision_history_ProcedureId",
                table: "verification_impact_decision_history",
                column: "ProcedureId");

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_decision_history_ProcedureRevisionId",
                table: "verification_impact_decision_history",
                column: "ProcedureRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_verification_impact_decision_history_VerificationImpactItem~",
                table: "verification_impact_decision_history",
                columns: new[] { "VerificationImpactItemId", "OccurredAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_verification_impact_items_test_procedure_revisions_Resolved~",
                table: "verification_impact_items",
                column: "ResolvedProcedureRevisionId",
                principalTable: "test_procedure_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_verification_impact_items_test_procedures_ResolvedProcedure~",
                table: "verification_impact_items",
                column: "ResolvedProcedureId",
                principalTable: "test_procedures",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_verification_impact_items_test_procedure_revisions_Resolved~",
                table: "verification_impact_items");

            migrationBuilder.DropForeignKey(
                name: "FK_verification_impact_items_test_procedures_ResolvedProcedure~",
                table: "verification_impact_items");

            migrationBuilder.DropTable(
                name: "verification_impact_decision_history");

            migrationBuilder.DropIndex(
                name: "IX_verification_impact_items_ResolvedProcedureId",
                table: "verification_impact_items");

            migrationBuilder.DropIndex(
                name: "IX_verification_impact_items_ResolvedProcedureRevisionId",
                table: "verification_impact_items");

            migrationBuilder.DropColumn(
                name: "ResolvedProcedureRevisionId",
                table: "verification_impact_items");
        }
    }
}
