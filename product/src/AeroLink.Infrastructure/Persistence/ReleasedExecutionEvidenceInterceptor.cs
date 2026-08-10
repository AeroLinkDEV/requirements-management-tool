using System.Data;
using System.Globalization;
using AeroLink.Domain.Releases;
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
                using var command = connection.CreateCommand();
                if (db.Database.CurrentTransaction is { } transaction)
                    command.Transaction = transaction.GetDbTransaction();
                command.CommandText = ReleaseLookupSql;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@executionId";
                parameter.Value = executionId;
                command.Parameters.Add(parameter);
                if (IsReleased(command.ExecuteScalar())) ThrowReleased();
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
                await using var command = connection.CreateCommand();
                if (db.Database.CurrentTransaction is { } transaction)
                    command.Transaction = transaction.GetDbTransaction();
                command.CommandText = ReleaseLookupSql;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@executionId";
                parameter.Value = executionId;
                command.Parameters.Add(parameter);
                if (IsReleased(await command.ExecuteScalarAsync(cancellationToken))) ThrowReleased();
            }
        }
        finally
        {
            if (closeAfter) await connection.CloseAsync();
        }
    }

    private static bool IsReleased(object? value) =>
        value is not null
        && value is not DBNull
        && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static void ThrowReleased() =>
        throw new ReleasedBuildReadOnlyException(
            "This execution belongs to a released build and its evidence relationships are read-only. "
            + "Use an explicit post-release amendment workflow for any correction.");

    // ReleaseId is recorded on current executions. The build join is a truthful fallback for legacy rows that
    // predate execution release scope but still name the exact SoftwareBuild they were run against.
    private const string ReleaseLookupSql = """
        SELECT r."IsReleased"
        FROM "test_executions" AS e
        LEFT JOIN "software_builds" AS b ON b."Id" = e."SoftwareBuildId"
        LEFT JOIN "releases" AS r ON r."Id" = COALESCE(e."ReleaseId", b."ReleaseId")
        WHERE e."Id" = @executionId
        """;
}
