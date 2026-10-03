using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Narrow upgrade for existing disposable/local SQLite schemas that use EnsureCreated. PostgreSQL
/// receives the authoritative additive migration. Installation checks, derived columns and constraints
/// share one immediate transaction; source text and historical evidence are never rewritten.</summary>
public static class ProblemReportImportSqliteGuard
{
    public static async Task EnsureInstalledAsync(AeroLinkDbContext db, CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite()) return;
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var ownedOpen = connection.State != ConnectionState.Open;
        if (ownedOpen) await connection.OpenAsync(ct);
        ProblemReportSourceIdentityConnectionInterceptor.Register(connection);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await ValidateStoredEncoding(connection, transaction, ct);
            var invalid = await Scalar(connection, transaction, """
                SELECT "Id" FROM problem_reports WHERE NOT (
                  ("SourceSystem" IS NULL AND "SourceKey" IS NULL) OR
                  ("SourceSystem" IS NOT NULL AND "SourceKey" IS NOT NULL
                   AND length(aerolink_source_identity_v1(CAST("SourceSystem" AS BLOB),0,CAST('A' AS BLOB))) > 0
                   AND length(aerolink_source_identity_v1(CAST("SourceKey" AS BLOB),1,CAST('A' AS BLOB))) > 0
                   AND aerolink_source_trimmed_v1(CAST("SourceSystem" AS BLOB),CAST('A' AS BLOB)) = 1
                   AND aerolink_source_trimmed_v1(CAST("SourceKey" AS BLOB),CAST('A' AS BLOB)) = 1)) LIMIT 1
                """, ct);
            if (invalid is not null)
                throw new InvalidOperationException($"Problem Report source identity installation refused invalid source shape on report {invalid}. Original records were preserved.");
            var duplicate = await Scalar(connection, transaction, """
                SELECT group_concat("Id", ',') FROM problem_reports WHERE "SourceKey" IS NOT NULL
                GROUP BY "ProjectId", aerolink_source_identity_v1(CAST("SourceSystem" AS BLOB),0,CAST('A' AS BLOB)),
                    aerolink_source_identity_v1(CAST("SourceKey" AS BLOB),1,CAST('A' AS BLOB)) HAVING count(*) > 1 LIMIT 1
                """, ct);
            if (duplicate is not null)
                throw new InvalidOperationException($"Problem Report source identity installation refused conflicting reports {duplicate}. Original records were preserved.");

            // Identity-dependent upgrades admit only a runtime matching the frozen v1 comparer. Existing
            // read keys use pure frozen functions and remain readable if later runtime compatibility fails.
            var sourceColumns = await ColumnExists(connection, transaction, "problem_reports", "SourceKeyIdentityV1", ct);
            if (!sourceColumns)
            {
                ProblemReportSourceIdentityKey.EnsureCompatible();
                await Execute(connection, transaction, """
                    ALTER TABLE problem_reports ADD COLUMN "SourceSystemIdentityV1" BLOB GENERATED ALWAYS AS (aerolink_source_identity_v1(CAST("SourceSystem" AS BLOB),0,CAST('A' AS BLOB))) VIRTUAL;
                    ALTER TABLE problem_reports ADD COLUMN "SourceKeyIdentityV1" BLOB GENERATED ALWAYS AS (aerolink_source_identity_v1(CAST("SourceKey" AS BLOB),1,CAST('A' AS BLOB))) VIRTUAL;
                    """, ct);
            }
            if (!await ColumnExists(connection, transaction, "problem_report_import_batches", "OperationId", ct))
                await Execute(connection, transaction, """
                    ALTER TABLE problem_report_import_batches ADD COLUMN "OperationId" TEXT NULL;
                    ALTER TABLE problem_report_import_batches ADD COLUMN "ActorId" TEXT NULL;
                    ALTER TABLE problem_report_import_batches ADD COLUMN "RequestHash" TEXT NULL;
                    ALTER TABLE problem_report_import_batches ADD COLUMN "ReceiptJson" TEXT NULL;
                    """, ct);
            await Execute(connection, transaction, """
                CREATE UNIQUE INDEX IF NOT EXISTS ux_pr_source_identity ON problem_reports ("ProjectId", "SourceSystemIdentityV1", "SourceKeyIdentityV1") WHERE "SourceKeyIdentityV1" IS NOT NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS ux_pr_import_operation ON problem_report_import_batches ("ProjectId", "ActorId", "OperationId") WHERE "OperationId" IS NOT NULL;
                CREATE TRIGGER IF NOT EXISTS aerolink_pr_source_shape_ins BEFORE INSERT ON problem_reports FOR EACH ROW WHEN NOT (
                    (NEW."SourceSystem" IS NULL AND NEW."SourceKey" IS NULL) OR (NEW."SourceSystem" IS NOT NULL AND NEW."SourceKey" IS NOT NULL
                    AND length(NEW."SourceSystemIdentityV1") > 0 AND length(NEW."SourceKeyIdentityV1") > 0
                    AND aerolink_source_trimmed_v1(CAST(NEW."SourceSystem" AS BLOB),CAST('A' AS BLOB)) = 1 AND aerolink_source_trimmed_v1(CAST(NEW."SourceKey" AS BLOB),CAST('A' AS BLOB)) = 1))
                    BEGIN SELECT RAISE(ABORT, 'Problem Report source identity must be a complete nonempty trimmed pair'); END;
                CREATE TRIGGER IF NOT EXISTS aerolink_pr_source_shape_upd BEFORE UPDATE ON problem_reports FOR EACH ROW WHEN NOT (
                    (NEW."SourceSystem" IS NULL AND NEW."SourceKey" IS NULL) OR (NEW."SourceSystem" IS NOT NULL AND NEW."SourceKey" IS NOT NULL
                    AND length(NEW."SourceSystemIdentityV1") > 0 AND length(NEW."SourceKeyIdentityV1") > 0
                    AND aerolink_source_trimmed_v1(CAST(NEW."SourceSystem" AS BLOB),CAST('A' AS BLOB)) = 1 AND aerolink_source_trimmed_v1(CAST(NEW."SourceKey" AS BLOB),CAST('A' AS BLOB)) = 1))
                    BEGIN SELECT RAISE(ABORT, 'Problem Report source identity must be a complete nonempty trimmed pair'); END;
                CREATE TRIGGER IF NOT EXISTS aerolink_pr_receipt_shape_ins BEFORE INSERT ON problem_report_import_batches FOR EACH ROW WHEN NOT (
                    (NEW."OperationId" IS NULL AND NEW."ActorId" IS NULL AND NEW."RequestHash" IS NULL AND NEW."ReceiptJson" IS NULL) OR
                    (NEW."OperationId" IS NOT NULL AND NEW."ActorId" IS NOT NULL AND NEW."OperationId" <> '00000000-0000-0000-0000-000000000000' AND NEW."ActorId" <> '00000000-0000-0000-0000-000000000000'
                     AND NEW."RequestHash" IS NOT NULL AND NEW."ReceiptJson" IS NOT NULL AND length(NEW."RequestHash") = 64 AND length(NEW."ReceiptJson") > 0))
                    BEGIN SELECT RAISE(ABORT, 'Problem Report import operation requires its complete original receipt'); END;
                CREATE TRIGGER IF NOT EXISTS aerolink_pr_receipt_immutable_upd BEFORE UPDATE ON problem_report_import_batches FOR EACH ROW
                    BEGIN SELECT RAISE(ABORT, 'Problem Report import receipts are immutable'); END;
                CREATE TRIGGER IF NOT EXISTS aerolink_pr_receipt_immutable_del BEFORE DELETE ON problem_report_import_batches FOR EACH ROW
                    BEGIN SELECT RAISE(ABORT, 'Problem Report import receipts are immutable'); END;
                """, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* Keep the refusal. */ }
            throw;
        }
        finally { if (ownedOpen) await connection.CloseAsync(); }
    }

    private static async Task ValidateStoredEncoding(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT \"Id\", CAST(\"SourceSystem\" AS BLOB), CAST(\"SourceKey\" AS BLOB), CAST('A' AS BLOB) FROM problem_reports";
        await using var rows = await command.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            try
            {
                var marker = (byte[])rows[3];
                if (!rows.IsDBNull(1)) ProblemReportSourceIdentityConnectionInterceptor.Decode((byte[])rows[1], marker);
                if (!rows.IsDBNull(2)) ProblemReportSourceIdentityConnectionInterceptor.Decode((byte[])rows[2], marker);
            }
            catch (System.Text.DecoderFallbackException ex)
            {
                throw new InvalidOperationException($"Problem Report source identity installation refused invalid stored text encoding on report {rows[0]}. Original records were preserved.", ex);
            }
        }
    }
    private static async Task<bool> ColumnExists(SqliteConnection connection, SqliteTransaction transaction,
        string table, string column, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM pragma_table_xinfo(@table) WHERE name = @column";
        command.Parameters.AddWithValue("table", table); command.Parameters.AddWithValue("column", column);
        return (long)(await command.ExecuteScalarAsync(ct))! != 0;
    }
    private static async Task<object?> Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    { await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return await command.ExecuteScalarAsync(ct); }
    private static async Task Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    { await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; await command.ExecuteNonQueryAsync(ct); }
}
