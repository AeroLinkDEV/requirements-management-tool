using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Installs the Release picker membership safeguards on SQLite hosts, whose schema comes from
/// EnsureCreatedAsync and therefore never runs migration SQL. First installation adds the legacy-cohort
/// flag, classifies existing NULL-membership rows, and creates the triggers inside one immediate
/// transaction; later installations only ensure the triggers exist and never reclassify rows.
/// PostgreSQL receives its equivalent guards through the additive migration.
/// </summary>
public static class ReleasePickerSqliteGuard
{
    public static async Task EnsureInstalledAsync(AeroLinkDbContext db, CancellationToken cancellationToken = default)
    {
        if (db.Database.IsNpgsql()) return;
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var ownedOpen = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            ownedOpen = true;
        }
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var transactionDisposal = transaction;
        try
        {
            var firstInstallation = 0 == (long)(await ScalarAsync(connection, (SqliteTransaction)transaction,
                "SELECT COUNT(*) FROM pragma_table_info('software_releases') WHERE name = 'PickerLegacyCohort'", cancellationToken))!;
            if (firstInstallation)
            {
                await ExecuteAsync(connection, (SqliteTransaction)transaction,
                    "ALTER TABLE \"software_releases\" ADD COLUMN \"PickerLegacyCohort\" INTEGER NOT NULL DEFAULT 0 CHECK (\"PickerLegacyCohort\" IN (0,1))",
                    cancellationToken);
                // Existing rows are the documented legacy cohort: present when this feature's schema was
                // installed. Marking happens before the triggers exist, inside this same transaction.
                await ExecuteAsync(connection, (SqliteTransaction)transaction,
                    "UPDATE \"software_releases\" SET \"PickerLegacyCohort\" = 1 WHERE \"PickerInsertionOrdinal\" IS NULL",
                    cancellationToken);
            }
            await ExecuteAsync(connection, (SqliteTransaction)transaction,
                """CREATE TRIGGER IF NOT EXISTS aerolink_release_picker_supplied_ins BEFORE INSERT ON "software_releases" FOR EACH ROW WHEN NEW."PickerInsertionOrdinal" IS NOT NULL OR NEW."PickerLegacyCohort" <> 0 BEGIN SELECT RAISE(ABORT, 'picker insertion ordinal and cohort flag are database-owned'); END""",
                cancellationToken);
            await ExecuteAsync(connection, (SqliteTransaction)transaction,
                """CREATE TRIGGER IF NOT EXISTS aerolink_release_picker_alloc_ins AFTER INSERT ON "software_releases" FOR EACH ROW WHEN NEW."PickerInsertionOrdinal" IS NULL BEGIN UPDATE "software_releases" SET "PickerInsertionOrdinal" = (SELECT COALESCE(MAX("PickerInsertionOrdinal"), 0) + 1 FROM "software_releases" WHERE "ProjectId" = NEW."ProjectId") WHERE "Id" = NEW."Id"; END""",
                cancellationToken);
            await ExecuteAsync(connection, (SqliteTransaction)transaction,
                """CREATE TRIGGER IF NOT EXISTS aerolink_release_picker_immutable_upd BEFORE UPDATE ON "software_releases" FOR EACH ROW WHEN NEW."PickerLegacyCohort" IS NOT OLD."PickerLegacyCohort" OR (NEW."PickerInsertionOrdinal" IS NOT OLD."PickerInsertionOrdinal" AND NOT (OLD."PickerLegacyCohort" = 0 AND OLD."PickerInsertionOrdinal" IS NULL AND NEW."PickerInsertionOrdinal" IS NOT NULL)) BEGIN SELECT RAISE(ABORT, 'picker insertion membership is immutable'); END""",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* Preserve the original installation failure. */ }
            throw;
        }
        finally
        {
            if (ownedOpen) await connection.CloseAsync();
        }
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction,
        string text, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string text, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
