using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AeroLink.Api.Tests;

/// <summary>
/// Coordinates two in-flight <c>SaveChanges</c> calls so a test can prove the true EF concurrency-token
/// collision branch of a governed mutation, rather than only the pre-check that fires when the first
/// request has already completed. Both requests load the same Version, both then save, and the concurrency
/// token refuses the second write.
///
/// Gates are keyed by database connection string, so parallel xUnit classes running against their own
/// throwaway SQLite files never touch one another's gate.
/// </summary>
public sealed class SaveRaceInterceptor : ISaveChangesInterceptor
{
    private static readonly ConcurrentDictionary<string, SaveRaceGate> ActiveGates =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void Activate(SaveRaceGate gate) => ActiveGates[gate.ConnectionString] = gate;

    internal static void Deactivate(SaveRaceGate gate) =>
        ActiveGates.TryRemove(new KeyValuePair<string, SaveRaceGate>(gate.ConnectionString, gate));

    public async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is null) return result;
        var connection = eventData.Context.Database.GetDbConnection()?.ConnectionString;
        if (connection is null || !ActiveGates.TryGetValue(connection, out var gate)) return result;
        await gate.EnterAsync(cancellationToken);
        return result;
    }
}

public sealed class SaveRaceGate : IDisposable
{
    public string ConnectionString { get; }

    public SaveRaceGate(string connectionString)
    {
        ConnectionString = connectionString;
        SaveRaceInterceptor.Activate(this);
    }

    private readonly SemaphoreSlim _firstEntered = new(0, 1);
    private readonly SemaphoreSlim _secondEntered = new(0, 1);
    private readonly SemaphoreSlim _releaseFirst = new(0, 1);
    private readonly SemaphoreSlim _releaseSecond = new(0, 1);
    private int _entered;
    private bool _disposed;

    /// <summary>The first request has reached its SaveChanges with the record already loaded.</summary>
    public Task<bool> FirstEnteredAsync(TimeSpan timeout) => _firstEntered.WaitAsync(timeout);

    /// <summary>The second request has reached its SaveChanges with the record already loaded.</summary>
    public Task<bool> SecondEnteredAsync(TimeSpan timeout) => _secondEntered.WaitAsync(timeout);

    public void ReleaseFirst() => _releaseFirst.Release();
    public void ReleaseSecond() => _releaseSecond.Release();

    internal async Task EnterAsync(CancellationToken cancellationToken)
    {
        var position = Interlocked.Increment(ref _entered);
        if (position == 1)
        {
            _firstEntered.Release();
            await _releaseFirst.WaitAsync(cancellationToken);
        }
        else
        {
            _secondEntered.Release();
            await _releaseSecond.WaitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SaveRaceInterceptor.Deactivate(this);
        _firstEntered.Dispose();
        _secondEntered.Dispose();
        _releaseFirst.Dispose();
        _releaseSecond.Dispose();
    }
}
