using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Takes the database lock that arbitrates an exclusive edit session.
///
/// A serializable transaction alone does not make a preceding read a commit-time decision: on PostgreSQL a
/// normal SELECT does not lock the row, and SQLite defers its writer lock until the first write. A provider-
/// neutral no-op UPDATE acquires a row lock on PostgreSQL and SQLite's write lock, after which the caller must
/// re-read and validate the session before committing the operation. The value is deliberately unchanged so
/// this synchronization primitive cannot fabricate activity or advance the session token.
/// </summary>
public static class ArtifactEditSessionLock
{
    public static async Task<ArtifactEditSession?> AcquireAsync(AeroLinkDbContext db, Guid sessionId,
        CancellationToken ct)
    {
        // A caller may hold an instance from a preceding transaction (the inline-image upload deliberately
        // has two). ExecuteUpdate bypasses the change tracker, so detach that stale instance before the
        // commit-time re-read can otherwise return yesterday's state from the identity map.
        var tracked = db.ChangeTracker.Entries<ArtifactEditSession>()
            .FirstOrDefault(entry => entry.Entity.Id == sessionId);
        if (tracked is not null)
            tracked.State = EntityState.Detached;
        var affected = await db.ArtifactEditSessions
            .Where(session => session.Id == sessionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.Version, session => session.Version), ct);
        return affected == 0
            ? null
            : await db.ArtifactEditSessions.SingleOrDefaultAsync(session => session.Id == sessionId, ct);
    }
}
