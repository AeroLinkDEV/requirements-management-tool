using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProblemReportImportRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RequirePostgres();
            ProblemReportSourceIdentityKey.EnsureCompatible();
            // Freeze v1 independently of the executing runtime and take the table lock before inspecting
            // existing identities. Check, generated-column backfill and constraints stay in EF's transaction.
            migrationBuilder.Sql("""
                DO $encoding$ BEGIN
                  IF current_setting('server_encoding') <> 'UTF8' THEN
                    RAISE EXCEPTION 'Problem Report source identity v1 requires UTF8 PostgreSQL server encoding';
                  END IF;
                END $encoding$;
                LOCK TABLE problem_reports, problem_report_import_batches IN ACCESS EXCLUSIVE MODE;
                """);
            using (var resource = typeof(AddProblemReportImportRecovery).Assembly.GetManifestResourceStream(
                "AeroLink.Infrastructure.Persistence.Migrations.ProblemReportSourceIdentityV1.sql")
                ?? throw new InvalidOperationException("The frozen source identity v1 migration function is missing."))
            using (var reader = new System.IO.StreamReader(resource))
                migrationBuilder.Sql(reader.ReadToEnd());
            migrationBuilder.Sql("""
                DO $identity$ DECLARE affected text; BEGIN
                  SELECT string_agg("Id"::text, ',') INTO affected FROM problem_reports WHERE NOT (
                    ("SourceSystem" IS NULL AND "SourceKey" IS NULL) OR
                    ("SourceSystem" IS NOT NULL AND "SourceKey" IS NOT NULL
                     AND octet_length(aerolink_source_identity_v1("SourceSystem",false)) > 0
                     AND octet_length(aerolink_source_identity_v1("SourceKey",true)) > 0
                     AND char_length("SourceSystem") * 4 = octet_length(aerolink_source_identity_v1("SourceSystem",false))
                     AND char_length("SourceKey") * 4 = octet_length(aerolink_source_identity_v1("SourceKey",true))));
                  IF affected IS NOT NULL THEN
                    RAISE EXCEPTION 'Problem Report source identity upgrade refused invalid source shape on reports %. Original records preserved.', affected;
                  END IF;
                  SELECT string_agg(ids, ';') INTO affected FROM (
                    SELECT string_agg("Id"::text, ',') AS ids FROM problem_reports WHERE "SourceKey" IS NOT NULL
                    GROUP BY "ProjectId", aerolink_source_identity_v1("SourceSystem",false), aerolink_source_identity_v1("SourceKey",true)
                    HAVING count(*) > 1) conflicts;
                  IF affected IS NOT NULL THEN
                    RAISE EXCEPTION 'Problem Report source identity upgrade refused conflicting reports %. Original records preserved.', affected;
                  END IF;
                END $identity$;
                """);
            migrationBuilder.AddColumn<Guid>(
                name: "ActorId",
                table: "problem_report_import_batches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "problem_report_import_batches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptJson",
                table: "problem_report_import_batches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "problem_report_import_batches",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SourceKeyIdentityV1",
                table: "problem_reports",
                type: "bytea",
                nullable: true,
                computedColumnSql: "aerolink_source_identity_v1(\"SourceKey\",true)",
                stored: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SourceSystemIdentityV1",
                table: "problem_reports",
                type: "bytea",
                nullable: true,
                computedColumnSql: "aerolink_source_identity_v1(\"SourceSystem\",false)",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ux_pr_source_identity",
                table: "problem_reports",
                columns: new[] { "ProjectId", "SourceSystemIdentityV1", "SourceKeyIdentityV1" },
                unique: true,
                filter: "\"SourceKeyIdentityV1\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_pr_source_identity_shape",
                table: "problem_reports",
                sql: "(\"SourceSystem\" IS NULL AND \"SourceKey\" IS NULL) OR (\"SourceSystem\" IS NOT NULL AND \"SourceKey\" IS NOT NULL AND length(\"SourceSystemIdentityV1\") > 0 AND length(\"SourceKeyIdentityV1\") > 0 AND char_length(\"SourceSystem\") * 4 = octet_length(\"SourceSystemIdentityV1\") AND char_length(\"SourceKey\") * 4 = octet_length(\"SourceKeyIdentityV1\"))");

            migrationBuilder.CreateIndex(
                name: "ux_pr_import_operation",
                table: "problem_report_import_batches",
                columns: new[] { "ProjectId", "ActorId", "OperationId" },
                unique: true,
                filter: "\"OperationId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_pr_import_operation_receipt",
                table: "problem_report_import_batches",
                sql: "(\"OperationId\" IS NULL AND \"ActorId\" IS NULL AND \"RequestHash\" IS NULL AND \"ReceiptJson\" IS NULL) OR (\"OperationId\" IS NOT NULL AND \"ActorId\" IS NOT NULL AND \"OperationId\" <> '00000000-0000-0000-0000-000000000000' AND \"ActorId\" <> '00000000-0000-0000-0000-000000000000' AND \"RequestHash\" IS NOT NULL AND \"ReceiptJson\" IS NOT NULL AND length(\"RequestHash\") = 64 AND length(\"ReceiptJson\") > 0)");
            migrationBuilder.Sql("""
                CREATE FUNCTION aerolink_pr_import_receipt_immutable_v1() RETURNS trigger LANGUAGE plpgsql AS $immutable$
                BEGIN RAISE EXCEPTION 'Problem Report import receipts are immutable'; END $immutable$;
                CREATE TRIGGER aerolink_pr_import_receipt_immutable BEFORE UPDATE OR DELETE ON problem_report_import_batches
                  FOR EACH ROW EXECUTE FUNCTION aerolink_pr_import_receipt_immutable_v1();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RequirePostgres();
            migrationBuilder.Sql("""
                LOCK TABLE problem_reports, problem_report_import_batches IN ACCESS EXCLUSIVE MODE;
                DO $receipt$ BEGIN IF EXISTS (SELECT 1 FROM problem_report_import_batches WHERE "OperationId" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Cannot downgrade a database with recorded Problem Report import operation receipts';
                END IF; END $receipt$;
                DROP TRIGGER aerolink_pr_import_receipt_immutable ON problem_report_import_batches;
                DROP FUNCTION aerolink_pr_import_receipt_immutable_v1();
                """);
            migrationBuilder.DropIndex(
                name: "ux_pr_source_identity",
                table: "problem_reports");

            migrationBuilder.DropCheckConstraint(
                name: "CK_pr_source_identity_shape",
                table: "problem_reports");

            migrationBuilder.DropIndex(
                name: "ux_pr_import_operation",
                table: "problem_report_import_batches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_pr_import_operation_receipt",
                table: "problem_report_import_batches");

            migrationBuilder.DropColumn(
                name: "SourceKeyIdentityV1",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "SourceSystemIdentityV1",
                table: "problem_reports");

            migrationBuilder.DropColumn(
                name: "ActorId",
                table: "problem_report_import_batches");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "problem_report_import_batches");

            migrationBuilder.DropColumn(
                name: "ReceiptJson",
                table: "problem_report_import_batches");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "problem_report_import_batches");
            migrationBuilder.Sql("DROP FUNCTION aerolink_source_identity_v1(text,boolean);");
        }

        private void RequirePostgres()
        {
            if (ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
                throw new InvalidOperationException("This migration requires PostgreSQL. Disposable/local SQLite schemas use EnsureCreated and ProblemReportImportSqliteGuard.");
        }
    }
}
