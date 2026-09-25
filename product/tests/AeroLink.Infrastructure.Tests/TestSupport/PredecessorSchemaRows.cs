using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Seeds a predecessor schema for an upgrade qualification (#1122). The current model maps every column that later
/// migrations add, so <c>SaveChangesAsync</c> against an older schema tests today's schema instead, and fails as
/// soon as such a column appears (<c>software_releases.CanonicalIdentity</c> and
/// <c>requirement_revisions.SourceBaselineId</c> did).
///
/// Build the fixture with the domain types as usual and add it to the context, then call
/// <see cref="InsertTrackedAsync"/> instead of saving. Each added row is written in only the columns the connected
/// database has, so the predecessor's own defaults fill the rest, as they did for rows of that era.
/// </summary>
internal static class PredecessorSchemaRows
{
    /// <summary>
    /// Writes every entity the context tracks as added, then clears the tracker. It does not run the save boundary:
    /// that validates against the current schema, and the rows it would append belong to later features. Rows are
    /// retried until each one's parent exists, so the caller need not order them.
    /// </summary>
    public static async Task InsertTrackedAsync(AeroLinkDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var pending = new Queue<EntityEntry>(db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added));
        var columns = await ColumnsAsync(db);
        var failedInARow = 0;
        while (pending.Count > 0)
        {
            var entry = pending.Dequeue();
            try
            {
                await InsertAsync(db, entry, columns);
                failedInARow = 0;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && failedInARow < pending.Count)
            {
                // Its parent is still pending. Once a whole pass makes no progress, the violation is real.
                pending.Enqueue(entry);
                failedInARow++;
            }
        }

        db.ChangeTracker.Clear();
    }

    private static async Task InsertAsync(AeroLinkDbContext db, EntityEntry entry,
        IReadOnlyDictionary<string, HashSet<string>> columns)
    {
        var entityType = entry.Metadata;
        var table = entityType.GetTableName()
            ?? throw new InvalidOperationException($"{entityType.DisplayName()} is not mapped to a table.");
        if (!columns.TryGetValue(table, out var existing))
            throw new InvalidOperationException($"The predecessor schema has no table {table} for {entityType.DisplayName()}.");
        var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());

        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        var names = new List<string>();
        foreach (var property in entityType.GetProperties())
        {
            var column = property.GetColumnName(store);
            if (column is null || !existing.Contains(column) || entry.Property(property.Name).IsTemporary)
                continue;
            var parameter = property.GetRelationalTypeMapping().CreateParameter(command, $"p{names.Count}",
                entry.Property(property.Name).CurrentValue, property.IsNullable);
            command.Parameters.Add(parameter);
            names.Add(column);
        }

        command.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(x => $"\"{x}\""))}) "
            + $"VALUES ({string.Join(", ", names.Select((_, i) => $"@p{i}"))})";
        await db.Database.OpenConnectionAsync();
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyDictionary<string, HashSet<string>>> ColumnsAsync(AeroLinkDbContext db)
    {
        var rows = await db.Database.SqlQueryRaw<TableColumn>("""
            SELECT table_name AS "Table", column_name AS "Column"
            FROM information_schema.columns WHERE table_schema = 'public'
            """).ToListAsync();
        return rows.GroupBy(x => x.Table)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Column).ToHashSet(StringComparer.Ordinal));
    }

    private sealed record TableColumn(string Table, string Column);
}
