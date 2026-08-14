// Times SQLite connection opens inside the API test factory as a database-open sub-phase (#563).
//
// The host build already includes database open/schema initialization, so this sub-phase is informational
// and is never added again to the startup total by the aggregator (hostMs covers it). Telemetry I/O is
// best-effort: failures disable telemetry, never the test.

using System.Diagnostics;
using System.Data.Common;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AeroLink.Api.Tests;

internal sealed class TimingConnectionInterceptor(long factoryId, string callerFile, string callerMember) : DbConnectionInterceptor
{
    private readonly ConcurrentDictionary<DbConnection, Stopwatch> _opens = new();

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        _opens[connection] = Stopwatch.StartNew();
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        RecordOpen(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        RecordOpen(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData)
    {
        _opens.TryRemove(connection, out _);
        base.ConnectionFailed(connection, eventData);
    }

    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _opens.TryRemove(connection, out _);
        return base.ConnectionFailedAsync(connection, eventData, cancellationToken);
    }

    private void RecordOpen(DbConnection connection)
    {
        if (!_opens.TryRemove(connection, out var open)) return;
        open.Stop();
        ApiTestTelemetry.RecordFactoryPhase("dbOpen", 0, open.Elapsed.TotalMilliseconds, callerFile, callerMember, factoryId);
    }
}
