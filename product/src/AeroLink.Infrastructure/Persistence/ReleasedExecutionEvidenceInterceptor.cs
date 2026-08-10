using System.Data;
using System.Data.Common;
using System.Globalization;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Raised when ordinary product code tries to change the evidence relationships of an execution that belongs
/// to a released software configuration. The API translates this to the same released-build contract used by
/// the workspace guard; keeping the invariant here means a headerless endpoint, integration, seeder, or future
/// caller cannot bypass it by forgetting the optional workspace header.
/// </summary>
public sealed class ReleasedBuildReadOnlyException : InvalidOperationException
{
    public ReleasedBuildReadOnlyException(string message) : base(message) { }
}

/// <summary>
/// Makes released execution evidence immutable at the persistence boundary.
///
/// Evidence files remain Project records and can still be uploaded independently. What becomes immutable is
/// the relationship saying that a particular file was evidence for a particular released execution. Existing
/// historical links are read unchanged; only newly added links are inspected.
/// </summary>
public sealed class ReleasedExecutionEvidenceInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is AeroLinkDbContext db) RefuseReleasedExecutionLinks(db);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AeroLinkDbContext db)
            await RefuseReleasedExecutionLinksAsync(db, cancellationToken);
        return result;
    }

    private static IReadOnlyList<Guid> AddedExecutionIds(AeroLinkDbContext db) =>
        db.ChangeTracker.Entries<TestExecutionEvidence>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity.TestExecutionId)
            .Distinct()
            .ToList();

    private static void RefuseReleasedExecutionLinks(AeroLinkDbContext db)
    {
        var executionIds = AddedExecutionIds(db);
        if (executionIds.Count == 0) return;

        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) connection.Open();
        try
        {
            foreach (var executionId in executionIds)
            {
                var releaseId = ResolveReleaseId(db, connection, executionId);
                if (releaseId is Guid id && IsReleaseMarkedReleased(db, connection, id)) ThrowReleased();
            }
        }
        finally
        {
            if (closeAfter) connection.Close();
        }
    }

    private static async Task RefuseReleasedExecutionLinksAsync(
        AeroLinkDbContext db,
        CancellationToken cancellationToken)
    {
        var executionIds = AddedExecutionIds(db);
        if (executionIds.Count == 0) return;

        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await connection.OpenAsync(cancellationToken);
        try
        {
            foreach (var executionId in executionIds)
            {
                var releaseId = await ResolveReleaseIdAsync(db, connection, executionId, cancellationToken);
                if (releaseId is Guid id
                    && await IsReleaseMarkedReleasedAsync(db, connection, id, cancellationToken)) ThrowReleased();
            }
        }
        finally
        {
            if (closeAfter) await connection.CloseAsync();
        }
    }

    /// <summary>
    /// Resolve from tracked entities first. That closes the less-obvious bypass where a future caller adds a
    /// TestExecution and its TestExecutionEvidence relationship in the same SaveChanges: the execution does not
    /// exist in the database yet, but its release identity is already present in the unit of work.
    /// </summary>
    private static Guid? ResolveReleaseId(AeroLinkDbContext db, DbConnection connection, Guid executionId)
    {
        var tracked = db.ChangeTracker.Entries<TestExecution>()
            .FirstOrDefault(entry => entry.State != EntityState.Deleted && entry.Entity.Id == executionId)?.Entity;
        if (tracked is not null)
        {
            if (tracked.ReleaseId is Guid releaseId) return releaseId;
            if (tracked.SoftwareBuildId is not Guid buildId) return null;
            var trackedBuild = db.ChangeTracker.Entries<SoftwareBuild>()
                .FirstOrDefault(entry => entry.State != EntityState.Deleted && entry.Entity.Id == buildId)?.Entity;
            return trackedBuild?.ReleaseId ?? QueryGuid(db, connection, BuildReleaseLookupSql, "@buildId", buildId);
        }

        return QueryGuid(db, connection, ExecutionReleaseLookupSql, "@executionId", executionId);
    }

    private static async Task<Guid?> ResolveReleaseIdAsync(AeroLinkDbContext db, DbConnection connection,
        Guid executionId, CancellationToken cancellationToken)
    {
        var tracked = db.ChangeTracker.Entries<TestExecution>()
            .FirstOrDefault(entry => entry.State != EntityState.Deleted && entry.Entity.Id == executionId)?.Entity;
        if (tracked is not null)
        {
            if (tracked.ReleaseId is Guid releaseId) return releaseId;
            if (tracked.SoftwareBuildId is not Guid buildId) return null;
            var trackedBuild = db.ChangeTracker.Entries<SoftwareBuild>()
                .FirstOrDefault(entry => entry.State != EntityState.Deleted && entry.Entity.Id == buildId)?.Entity;
            return trackedBuild?.ReleaseId
                ?? await QueryGuidAsync(db, connection, BuildReleaseLookupSql, "@buildId", buildId, cancellationToken);
        }

        return await QueryGuidAsync(db, connection, ExecutionReleaseLookupSql, "@executionId", executionId,
            cancellationToken);
    }

    private static bool IsReleaseMarkedReleased(AeroLinkDbContext db, DbConnection connection, Guid releaseId)
    {
        var tracked = db.ChangeTracker.Entries<SoftwareRelease>()
            .FirstOrDefault(entry => entry.State != EntityState.Deleted && entry.Entity.Id == releaseId);
        // Added/Modified state is the current unit of work and has to win, including a release transition in
        // the same SaveChanges. An Unchanged entity may be stale if another context released the build after it
        // was loaded, so the database remains authoritative in that case.
        if (tracked is { State: EntityState.Added or EntityState.Modified }) return tracked.Entity.IsReleased;
        return IsReleased(QueryScalar(db, connection, ReleaseStateLookupSql, "@releaseId", releaseId));
    }

    private static async Task<bool> IsReleaseMarkedReleasedAsync(AeroLinkDbContext db, DbConnection connection,
        Guid releaseId, CancellationToken cancellationToken)
    {
        var tracked = db.ChangeTracker.Entries<SoftwareRelease>()
            .FirstOrDefault(entry => entry.State != EntityState.Deleted && entry.Entity.Id == releaseId);
        if (tracked is { State: EntityState.Added or EntityState.Modified }) return tracked.Entity.IsReleased;
        return IsReleased(await QueryScalarAsync(db, connection, ReleaseStateLookupSql, "@releaseId", releaseId,
            cancellationToken));
    }

    private static Guid? QueryGuid(AeroLinkDbContext db, DbConnection connection, string sql,
        string parameterName, Guid value) => AsGuid(QueryScalar(db, connection, sql, parameterName, value));

    private static async Task<Guid?> QueryGuidAsync(AeroLinkDbContext db, DbConnection connection, string sql,
        string parameterName, Guid value, CancellationToken cancellationToken) =>
        AsGuid(await QueryScalarAsync(db, connection, sql, parameterName, value, cancellationToken));

    private static object? QueryScalar(AeroLinkDbContext db, DbConnection connection, string sql,
        string parameterName, Guid value)
    {
        using var command = Command(db, connection, sql, parameterName, value);
        return command.ExecuteScalar();
    }

    private static async Task<object?> QueryScalarAsync(AeroLinkDbContext db, DbConnection connection, string sql,
        string parameterName, Guid value, CancellationToken cancellationToken)
    {
        await using var command = Command(db, connection, sql, parameterName, value);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static DbCommand Command(AeroLinkDbContext db, DbConnection connection, string sql,
        string parameterName, Guid value)
    {
        var command = connection.CreateCommand();
        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return command;
    }

    private static Guid? AsGuid(object? value)
    {
        if (value is Guid guid) return guid;
        return value is not null && value is not DBNull && Guid.TryParse(value.ToString(), out var parsed)
            ? parsed
            : null;
    }

    private static bool IsReleased(object? value) =>
        value is not null
        && value is not DBNull
        && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static void ThrowReleased() =>
        throw new ReleasedBuildReadOnlyException(
            "This execution belongs to a released build and its evidence relationships are read-only. "
            + "Use an explicit post-release amendment workflow for any correction.");

    // These names are the explicit ToTable mappings in AeroLinkDbContext and are shared by SQLite/PostgreSQL.
    private const string ExecutionReleaseLookupSql = """
        SELECT COALESCE(e."ReleaseId", b."ReleaseId")
        FROM "test_executions" AS e
        LEFT JOIN "software_builds" AS b ON b."Id" = e."SoftwareBuildId"
        WHERE e."Id" = @executionId
        """;

    private const string BuildReleaseLookupSql = """
        SELECT b."ReleaseId"
        FROM "software_builds" AS b
        WHERE b."Id" = @buildId
        """;

    private const string ReleaseStateLookupSql = """
        SELECT r."IsReleased"
        FROM "software_releases" AS r
        WHERE r."Id" = @releaseId
        """;
}
