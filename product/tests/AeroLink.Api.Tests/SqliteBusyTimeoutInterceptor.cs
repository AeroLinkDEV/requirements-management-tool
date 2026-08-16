using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AeroLink.Api.Tests;

/// <summary>
/// Configures every connection opened by an API test's disposable SQLite database. The setting is per
/// connection, so configuring only the factory's setup connection would leave host and test-scoped
/// contexts exposed to the default zero busy timeout.
/// </summary>
internal sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
{
    // This is deliberately independent from AeroLinkApiFactory.CommandTimeoutSeconds. CommandTimeout bounds
    // the whole command; this PRAGMA only gives SQLite's per-connection busy handler time to clear a lock.
    internal const int BusyTimeoutSeconds = 60;
    internal const int BusyTimeoutMilliseconds = BusyTimeoutSeconds * 1000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Configure(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Configure(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Configure(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;
        using var command = sqlite.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
    }
}
