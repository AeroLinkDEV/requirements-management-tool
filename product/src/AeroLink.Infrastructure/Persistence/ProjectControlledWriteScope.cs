using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Owns the short local transaction used to serialize project-scoped controlled writes.
///
/// The scope is deliberately explicit. A database transaction that happens to be present on the context is
/// not enough to prove that the caller acquired the project lock, so nested services must receive this scope
/// and join it rather than discovering an ambient transaction for themselves.
/// </summary>
public sealed class ProjectControlledWriteScope : IAsyncDisposable
{
    private readonly AeroLinkDbContext _db;
    private readonly IDbContextTransaction _transaction;
    private bool _completed;

    private ProjectControlledWriteScope(AeroLinkDbContext db, Guid projectId, IDbContextTransaction transaction)
    {
        _db = db;
        ProjectId = projectId;
        _transaction = transaction;
    }

    public Guid ProjectId { get; }
    public AeroLinkDbContext Db => _db;
    public IDbContextTransaction Transaction => _transaction;

    /// <summary>
    /// Starts a provider-aware write transaction and locks the existing project row before any aggregate is tracked.
    /// PostgreSQL uses FOR NO KEY UPDATE; SQLite receives a serializable provider transaction, which maps to a
    /// non-deferred write transaction in Microsoft.Data.Sqlite.
    /// </summary>
    public static async Task<ProjectControlledWriteScope> AcquireAsync(
        AeroLinkDbContext db, Guid projectId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (projectId == Guid.Empty) throw new ArgumentException("A project identity is required.", nameof(projectId));
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Acquire a project-controlled write scope before starting a database transaction, or explicitly join an existing scope.");

        var isolation = db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
        var transaction = await db.Database.BeginTransactionAsync(isolation, cancellationToken);
        try
        {
            await LockProjectRowAsync(db, projectId, cancellationToken);
            return new ProjectControlledWriteScope(db, projectId, transaction);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>Joins an already acquired scope after checking the context and project identity explicitly.</summary>
    public static ProjectControlledWriteScope Join(
        AeroLinkDbContext db, Guid projectId, ProjectControlledWriteScope scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        scope.EnsureJoined(db, projectId);
        return scope;
    }

    /// <summary>Requires an explicitly supplied scope for a nested controlled write.</summary>
    public static ProjectControlledWriteScope Require(
        AeroLinkDbContext db, Guid projectId, ProjectControlledWriteScope? scope)
        => scope is null
            ? throw new InvalidOperationException("This controlled write must join an explicit project-controlled write scope.")
            : Join(db, projectId, scope);

    /// <summary>
    /// Executes a project-scoped mutation with ownership of the transaction. The operation may call SaveChanges
    /// itself when it needs generated values before completion; this method performs the final save and commit.
    /// </summary>
    public static async Task<TResult> ExecuteAsync<TResult>(
        AeroLinkDbContext db,
        Guid projectId,
        Func<ProjectControlledWriteScope, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var scope = await AcquireAsync(db, projectId, cancellationToken);
        var result = await operation(scope);
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>Commits the owned transaction after the caller has performed its final save.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await _transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    /// <summary>
    /// Rolls back an owned scope when the caller knows that commit was never attempted. A successful return is the
    /// boundary at which staged external objects may be cleaned up; a failed rollback leaves the scope active so
    /// disposal can make a second best-effort attempt without claiming that the database outcome is known.
    /// </summary>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await _transaction.RollbackAsync(cancellationToken);
        _completed = true;
    }

    /// <summary>Checks that a nested operation uses this exact context, project and transaction.</summary>
    public void EnsureJoined(AeroLinkDbContext db, Guid projectId)
    {
        if (!ReferenceEquals(db, _db) || projectId != ProjectId)
            throw new InvalidOperationException("The nested operation does not belong to the active project-controlled write scope.");
        if (_db.Database.CurrentTransaction is null
            || !ReferenceEquals(_db.Database.CurrentTransaction.GetDbTransaction(), _transaction.GetDbTransaction()))
            throw new InvalidOperationException("The project-controlled write scope is not the active transaction on its DbContext.");
        EnsureActive();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try { await _transaction.RollbackAsync(CancellationToken.None); }
            catch { /* Preserve the original operation failure while still attempting cleanup. */ }
        }

        await _transaction.DisposeAsync();
        _completed = true;
    }

    private void EnsureActive()
    {
        if (_completed) throw new InvalidOperationException("The project-controlled write scope is already complete.");
    }

    private static async Task LockProjectRowAsync(AeroLinkDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = db.Database.IsNpgsql()
            ? "SELECT \"Id\" FROM \"projects\" WHERE \"Id\" = @project_id FOR NO KEY UPDATE"
            : "SELECT \"Id\" FROM \"projects\" WHERE \"Id\" = @project_id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@project_id";
        parameter.Value = projectId;
        command.Parameters.Add(parameter);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
            throw new KeyNotFoundException($"Project {projectId} was not found.");
    }
}
