using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AeroLink.Infrastructure.Diagnostics;

/// <summary>
/// Logs database work that takes longer than a threshold, and whose work it was (#939).
///
/// The hung requests all waited on something that only their cancellation ended, while the logs held no
/// failed or slow command, because EF logs a slow but successful command at Information and the harness keeps
/// that category at Warning to stay readable. This records only what crosses the threshold:
///
/// - a command, with its first statement;
/// - a connection open;
/// - a transaction held open too long, with the first statement it ran. A long-held write transaction is
///   exactly what would make other writers wait while readers carry on.
///
/// Each entry names the request that owned the work, or "background" for a hosted worker. Registered only
/// when <see cref="StallDiagnosticsSettings.SlowDatabaseMillisecondsKey"/> is set.
/// </summary>
public sealed class SlowDatabaseInterceptor(TimeSpan threshold, ILogger<SlowDatabaseInterceptor> logger)
    : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    private sealed class TransactionState(long startedAt, string owner)
    {
        public long StartedAt { get; } = startedAt;
        public string Owner { get; } = owner;
        public string? FirstStatement { get; set; }
    }

    private readonly ConditionalWeakTable<DbTransaction, TransactionState> _transactions = new();

    private void Command(DbCommand command, TimeSpan duration, string outcome)
    {
        if (command.Transaction is { } transaction && _transactions.TryGetValue(transaction, out var state))
            state.FirstStatement ??= Statement(command.CommandText);
        if (duration < threshold) return;
        logger.LogWarning("AEROLINK-SLOWDB command {Outcome} {DurationMs}ms owner={Owner} sql={Sql}",
            outcome, (long)duration.TotalMilliseconds, DiagnosticsWorkContext.Current, Statement(command.CommandText));
    }

    private static string Statement(string sql)
    {
        var flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 240 ? flat : flat[..240] + "…";
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    { Command(command, eventData.Duration, "read"); return result; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    { Command(command, eventData.Duration, "read"); return ValueTask.FromResult(result); }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    { Command(command, eventData.Duration, "write"); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    { Command(command, eventData.Duration, "write"); return ValueTask.FromResult(result); }
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    { Command(command, eventData.Duration, "scalar"); return result; }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        object? result, CancellationToken cancellationToken = default)
    { Command(command, eventData.Duration, "scalar"); return ValueTask.FromResult(result); }
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => Command(command, eventData.Duration, $"failed ({eventData.Exception.GetType().Name})");
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    { Command(command, eventData.Duration, $"failed ({eventData.Exception.GetType().Name})"); return Task.CompletedTask; }

    private void Opened(ConnectionEndEventData eventData)
    {
        if (eventData.Duration < threshold) return;
        logger.LogWarning("AEROLINK-SLOWDB connection-open {DurationMs}ms owner={Owner}",
            (long)eventData.Duration.TotalMilliseconds, DiagnosticsWorkContext.Current);
    }

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Opened(eventData);
    public Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    { Opened(eventData); return Task.CompletedTask; }

    private DbTransaction Started(DbTransaction transaction)
    {
        _transactions.AddOrUpdate(transaction, new TransactionState(Stopwatch.GetTimestamp(), DiagnosticsWorkContext.Current));
        return transaction;
    }

    private void Ended(DbTransaction transaction, string outcome)
    {
        if (!_transactions.TryGetValue(transaction, out var state)) return;
        _transactions.Remove(transaction);
        var held = Stopwatch.GetElapsedTime(state.StartedAt);
        if (held < threshold) return;
        logger.LogWarning("AEROLINK-SLOWDB transaction {Outcome} held {DurationMs}ms owner={Owner} first={Sql}",
            outcome, (long)held.TotalMilliseconds, state.Owner, state.FirstStatement ?? "(no statement)");
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        => Started(result);
    public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
        DbTransaction result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Started(result));
    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Ended(transaction, "committed");
    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    { Ended(transaction, "committed"); return Task.CompletedTask; }
    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Ended(transaction, "rolled back");
    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    { Ended(transaction, "rolled back"); return Task.CompletedTask; }
    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Ended(transaction, "failed");
    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    { Ended(transaction, "failed"); return Task.CompletedTask; }
}
