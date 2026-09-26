using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// An opt-in SQLite <c>synchronous</c> level, applied to every connection EF opens (#1163, #939).
///
/// The browser journeys' API host stalled for 10 to 40 s at a time with a login inside a SQLite commit. The
/// native stack showed <c>FlushFileBuffers</c>: the commit had pushed the WAL past SQLite's 1000-frame
/// auto-checkpoint, and the checkpoint's durability flush waited on a runner disk that was 300% busy, while
/// every later request queued behind it. That database is created for one browser run and deleted with it,
/// so the flushes protect nothing. The browser hosts set this to <c>Off</c>.
///
/// Unset, nothing is registered and SQLite keeps its own default. It is refused with PostgreSQL, where it
/// would silently do nothing, and for any value SQLite does not define.
/// </summary>
public sealed class SqliteSynchronousInterceptor(string level) : DbConnectionInterceptor
{
    public const string Key = "Database:SqliteSynchronous";
    private static readonly string[] Levels = ["Off", "Normal", "Full", "Extra"];

    public string Level { get; } = level;

    /// <summary>The configured level, or null when unset. Throws for a value SQLite does not define.</summary>
    public static string? Configured(IConfiguration configuration, bool isPostgres)
    {
        var value = configuration[Key];
        if (string.IsNullOrWhiteSpace(value)) return null;
        var level = Levels.FirstOrDefault(candidate => candidate.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{Key} is '{value}'. SQLite defines Off, Normal, Full and Extra.");
        if (isPostgres)
            throw new InvalidOperationException($"{Key} applies only to Database:Provider Sqlite.");
        return level;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Pragma(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = Pragma(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand Pragma(DbConnection connection)
    {
        var command = connection.CreateCommand();
        // Level is one of the four fixed names above, never caller text.
        command.CommandText = $"PRAGMA synchronous = {Level.ToUpperInvariant()}";
        return command;
    }
}
