using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Acquires the database row lock that arbitrates lifecycle and supporting-file changes for one Problem Report.
///
/// Serializable isolation detects many races, but a normal read is not a commit-time synchronization point on
/// PostgreSQL and SQLite defers its writer lock until the first write. An unchanged UPDATE gives both providers
/// an explicit lock before the caller validates the current aggregate, so approval and attachment mutation use
/// one deterministic boundary instead of relying on whichever request happened to read first.
/// </summary>
public static class ProblemReportLock
{
    public static async Task<ProblemReport?> AcquireAsync(AeroLinkDbContext db, Guid problemReportId,
        CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<ProblemReport>()
            .FirstOrDefault(entry => entry.Entity.Id == problemReportId);
        if (tracked is not null)
            tracked.State = EntityState.Detached;

        var affected = await db.ProblemReports
            .Where(report => report.Id == problemReportId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(report => report.Version, report => report.Version), ct);
        return affected == 0
            ? null
            : await db.ProblemReports.SingleOrDefaultAsync(report => report.Id == problemReportId, ct);
    }

    public static bool IsSerializationConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException)
                return true;

            // Keep the API project provider-neutral. Npgsql's PostgresException is intentionally inspected by
            // its stable type/property names so this library does not acquire a direct provider dependency.
            if (current.GetType().FullName == "Npgsql.PostgresException"
                && current.GetType().GetProperty("SqlState")?.GetValue(current) is string sqlState
                && sqlState is "40001" or "40P01")
                return true;

            if (current.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException"
                && current.GetType().GetProperty("SqliteErrorCode")?.GetValue(current) is int sqliteCode
                && sqliteCode is 5 or 6)
                return true;
        }

        return false;
    }
}
