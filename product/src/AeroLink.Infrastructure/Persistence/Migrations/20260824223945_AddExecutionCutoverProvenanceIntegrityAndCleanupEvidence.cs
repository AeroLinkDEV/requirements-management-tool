using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionCutoverProvenanceIntegrityAndCleanupEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_baseline_execution_cutover_provenance_candidate_baselines_B~",
                table: "baseline_execution_cutover_provenance");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_test_procedure_revisions_ProcedureId_Id",
                table: "test_procedure_revisions",
                columns: new[] { "ProcedureId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_baseline_events_BaselineId_Id",
                table: "baseline_events",
                columns: new[] { "BaselineId", "Id" });

            migrationBuilder.CreateTable(
                name: "rollback_cleanup_failure_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    TotalKeys = table.Column<int>(type: "integer", nullable: false),
                    CanonicalAggregateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rollback_cleanup_failure_evidence", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_revisions_ProcedureId_Id",
                table: "test_procedure_revisions",
                columns: new[] { "ProcedureId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_procedure_migration_sources_GeneratedProcedureArtifact~",
                table: "test_procedure_migration_sources",
                columns: new[] { "GeneratedProcedureArtifactId", "GeneratedProcedureRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_baseline_execution_cutover_provenance_BaselineId_EventId",
                table: "baseline_execution_cutover_provenance",
                columns: new[] { "BaselineId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_baseline_events_BaselineId_Id",
                table: "baseline_events",
                columns: new[] { "BaselineId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rollback_cleanup_failure_evidence_OperationId_Sequence",
                table: "rollback_cleanup_failure_evidence",
                columns: new[] { "OperationId", "Sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_baseline_execution_cutover_provenance_baseline_events_Basel~",
                table: "baseline_execution_cutover_provenance",
                columns: new[] { "BaselineId", "EventId" },
                principalTable: "baseline_events",
                principalColumns: new[] { "BaselineId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_baseline_execution_cutover_provenance_candidate_baselines_B~",
                table: "baseline_execution_cutover_provenance",
                column: "BaselineId",
                principalTable: "candidate_baselines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_test_procedure_migration_sources_test_procedure_revisions_G~",
                table: "test_procedure_migration_sources",
                column: "GeneratedProcedureRevisionId",
                principalTable: "test_procedure_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_test_procedure_migration_sources_test_procedure_revisions_S~",
                table: "test_procedure_migration_sources",
                column: "SourceCaseRevisionId",
                principalTable: "test_procedure_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_test_procedure_migration_sources_test_procedure_revisions_~1",
                table: "test_procedure_migration_sources",
                columns: new[] { "GeneratedProcedureArtifactId", "GeneratedProcedureRevisionId" },
                principalTable: "test_procedure_revisions",
                principalColumns: new[] { "ProcedureId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_test_procedure_migration_sources_test_procedures_GeneratedP~",
                table: "test_procedure_migration_sources",
                column: "GeneratedProcedureArtifactId",
                principalTable: "test_procedures",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // PostgreSQL-only triggers: ordinary foreign keys cannot express artifact kind, project ownership,
            // same-aggregate event type, or immutability. These make the controlled provenance fail closed at
            // the database boundary for raw SQL and every other persistence seam.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION aerolink_refuse_provenance_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Execution cutover provenance and migration sources are immutable historical evidence';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER refuse_baseline_provenance_mutation
                    BEFORE UPDATE OR DELETE ON baseline_execution_cutover_provenance
                    FOR EACH ROW EXECUTE FUNCTION aerolink_refuse_provenance_mutation();

                CREATE TRIGGER refuse_migration_source_mutation
                    BEFORE UPDATE OR DELETE ON test_procedure_migration_sources
                    FOR EACH ROW EXECUTE FUNCTION aerolink_refuse_provenance_mutation();

                CREATE OR REPLACE FUNCTION aerolink_enforce_provenance_event_kind() RETURNS trigger AS $$
                DECLARE
                    event_type text;
                BEGIN
                    SELECT e."EventType" INTO event_type
                    FROM baseline_events e
                    WHERE e."Id" = NEW."EventId";
                    IF event_type IS NULL OR event_type <> 'ExecutionCutoverManifestMigrated' THEN
                        RAISE EXCEPTION 'Execution cutover provenance must reference an ExecutionCutoverManifestMigrated event of the same baseline';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER enforce_provenance_event_kind
                    BEFORE INSERT OR UPDATE ON baseline_execution_cutover_provenance
                    FOR EACH ROW EXECUTE FUNCTION aerolink_enforce_provenance_event_kind();

                CREATE OR REPLACE FUNCTION aerolink_enforce_migration_source_integrity() RETURNS trigger AS $$
                DECLARE
                    source_kind text;
                    source_level text;
                    source_project uuid;
                    generated_kind text;
                    generated_project uuid;
                BEGIN
                    SELECT p."ArtifactKind", p."Level", p."ProjectId"
                    INTO source_kind, source_level, source_project
                    FROM test_procedure_revisions r
                    JOIN test_procedures p ON p."Id" = r."ProcedureId"
                    WHERE r."Id" = NEW."SourceCaseRevisionId";
                    IF source_kind IS NULL
                        OR source_kind <> 'Case'
                        OR source_level = 'System'
                        OR source_project <> NEW."ProjectId" THEN
                        RAISE EXCEPTION 'Migration source Case revision must be a software Case in the stated project';
                    END IF;
                    SELECT p."ArtifactKind", p."ProjectId"
                    INTO generated_kind, generated_project
                    FROM test_procedures p
                    WHERE p."Id" = NEW."GeneratedProcedureArtifactId";
                    IF generated_kind IS NULL
                        OR generated_kind <> 'Procedure'
                        OR generated_project <> NEW."ProjectId" THEN
                        RAISE EXCEPTION 'Migration generated artifact must be a Procedure in the stated project';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER enforce_migration_source_integrity
                    BEFORE INSERT OR UPDATE ON test_procedure_migration_sources
                    FOR EACH ROW EXECUTE FUNCTION aerolink_enforce_migration_source_integrity();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS enforce_migration_source_integrity ON test_procedure_migration_sources;
                DROP TRIGGER IF EXISTS refuse_migration_source_mutation ON test_procedure_migration_sources;
                DROP TRIGGER IF EXISTS enforce_provenance_event_kind ON baseline_execution_cutover_provenance;
                DROP TRIGGER IF EXISTS refuse_baseline_provenance_mutation ON baseline_execution_cutover_provenance;
                DROP FUNCTION IF EXISTS aerolink_enforce_migration_source_integrity();
                DROP FUNCTION IF EXISTS aerolink_enforce_provenance_event_kind();
                DROP FUNCTION IF EXISTS aerolink_refuse_provenance_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_baseline_execution_cutover_provenance_baseline_events_Basel~",
                table: "baseline_execution_cutover_provenance");

            migrationBuilder.DropForeignKey(
                name: "FK_baseline_execution_cutover_provenance_candidate_baselines_B~",
                table: "baseline_execution_cutover_provenance");

            migrationBuilder.DropForeignKey(
                name: "FK_test_procedure_migration_sources_test_procedure_revisions_G~",
                table: "test_procedure_migration_sources");

            migrationBuilder.DropForeignKey(
                name: "FK_test_procedure_migration_sources_test_procedure_revisions_S~",
                table: "test_procedure_migration_sources");

            migrationBuilder.DropForeignKey(
                name: "FK_test_procedure_migration_sources_test_procedure_revisions_~1",
                table: "test_procedure_migration_sources");

            migrationBuilder.DropForeignKey(
                name: "FK_test_procedure_migration_sources_test_procedures_GeneratedP~",
                table: "test_procedure_migration_sources");

            migrationBuilder.DropTable(
                name: "rollback_cleanup_failure_evidence");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_test_procedure_revisions_ProcedureId_Id",
                table: "test_procedure_revisions");

            migrationBuilder.DropIndex(
                name: "IX_test_procedure_revisions_ProcedureId_Id",
                table: "test_procedure_revisions");

            migrationBuilder.DropIndex(
                name: "IX_test_procedure_migration_sources_GeneratedProcedureArtifact~",
                table: "test_procedure_migration_sources");

            migrationBuilder.DropIndex(
                name: "IX_baseline_execution_cutover_provenance_BaselineId_EventId",
                table: "baseline_execution_cutover_provenance");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_baseline_events_BaselineId_Id",
                table: "baseline_events");

            migrationBuilder.DropIndex(
                name: "IX_baseline_events_BaselineId_Id",
                table: "baseline_events");

            migrationBuilder.AddForeignKey(
                name: "FK_baseline_execution_cutover_provenance_candidate_baselines_B~",
                table: "baseline_execution_cutover_provenance",
                column: "BaselineId",
                principalTable: "candidate_baselines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
