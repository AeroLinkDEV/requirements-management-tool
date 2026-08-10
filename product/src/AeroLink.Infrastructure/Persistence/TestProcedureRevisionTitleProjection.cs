using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The immutable human-readable title belonging to one exact procedure revision.
///
/// Controlled revisions produced by a TCR take their title from the exact approved procedure-change snapshot
/// that produced them. A retirement carries the nearest predecessor title because it withdraws that procedure
/// rather than asking the engineer to restate it. Revisions that predate controlled TCR provenance retain the
/// stable catalog title with an explicit compatibility note; the projection never invents historical precision.
/// </summary>
public sealed record TestProcedureRevisionTitleSnapshot(
    Guid RevisionId,
    string Title,
    bool IsExact,
    bool IsLegacy,
    string? Note);

public static class TestProcedureRevisionTitleProjection
{
    public static async Task<IReadOnlyDictionary<Guid, TestProcedureRevisionTitleSnapshot>> ForRevisionsAsync(
        AeroLinkDbContext db,
        IReadOnlyCollection<Guid> procedureRevisionIds,
        CancellationToken ct)
    {
        var requested = procedureRevisionIds.Where(x => x != Guid.Empty).Distinct().ToList();
        if (requested.Count == 0) return new Dictionary<Guid, TestProcedureRevisionTitleSnapshot>();

        var requestedRows = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                   join procedure in db.TestProcedures.AsNoTracking()
                                       on revision.ProcedureId equals procedure.Id
                                   where requested.Contains(revision.Id)
                                   select new
                                   {
                                       revision.Id,
                                       revision.ProcedureId,
                                       revision.Revision,
                                       revision.SourceTestChangeRequestId,
                                       procedure.BaseNumber,
                                       CatalogTitle = procedure.Title,
                                   }).ToListAsync(ct);
        var procedureIds = requestedRows.Select(x => x.ProcedureId).Distinct().ToList();

        // Retirement title inheritance may need a predecessor that was not part of the caller's bounded set,
        // so resolve the complete small revision chain for only the requested procedure identities.
        var chains = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                            join procedure in db.TestProcedures.AsNoTracking()
                                on revision.ProcedureId equals procedure.Id
                            where procedureIds.Contains(revision.ProcedureId)
                            select new
                            {
                                revision.Id,
                                revision.ProcedureId,
                                revision.Revision,
                                revision.SourceTestChangeRequestId,
                                procedure.BaseNumber,
                                CatalogTitle = procedure.Title,
                            }).ToListAsync(ct);
        var tcrIds = chains.Where(x => x.SourceTestChangeRequestId is not null)
            .Select(x => x.SourceTestChangeRequestId!.Value).Distinct().ToList();
        var changes = await db.Set<TestProcedureChange>().AsNoTracking()
            .Where(x => tcrIds.Contains(x.TestChangeReviewId))
            .Select(x => new
            {
                x.TestChangeReviewId,
                x.BaseNumber,
                x.Revision,
                x.Kind,
                x.Title,
            }).ToListAsync(ct);
        var changeByRevision = changes.ToDictionary(
            x => (x.TestChangeReviewId, x.BaseNumber.ToUpperInvariant(), x.Revision));

        var resolved = new Dictionary<Guid, TestProcedureRevisionTitleSnapshot>();
        foreach (var chain in chains.GroupBy(x => x.ProcedureId))
        {
            string? predecessorTitle = null;
            foreach (var revision in chain.OrderBy(x => x.Revision))
            {
                TestProcedureRevisionTitleSnapshot snapshot;
                if (revision.SourceTestChangeRequestId is not Guid sourceTcrId)
                {
                    snapshot = new(revision.Id, revision.CatalogTitle, false, true,
                        "Legacy revision — the exact historical title was not recorded on a controlled TCR snapshot; the stable catalog title is shown for compatibility.");
                }
                else if (changeByRevision.TryGetValue(
                             (sourceTcrId, revision.BaseNumber.ToUpperInvariant(), revision.Revision), out var change))
                {
                    if (!string.IsNullOrWhiteSpace(change.Title))
                    {
                        snapshot = new(revision.Id, change.Title.Trim(), true, false, null);
                    }
                    else if (change.Kind == TestProcedureChangeKind.Retire && !string.IsNullOrWhiteSpace(predecessorTitle))
                    {
                        snapshot = new(revision.Id, predecessorTitle, true, false,
                            "Retirement revision — title preserved from the exact predecessor being retired.");
                    }
                    else
                    {
                        snapshot = new(revision.Id, revision.CatalogTitle, false, false,
                            "The producing TCR is recorded, but its revision-title snapshot is unavailable; the stable catalog title is shown.");
                    }
                }
                else
                {
                    snapshot = new(revision.Id, revision.CatalogTitle, false, false,
                        "The producing TCR is recorded, but no matching procedure-change title snapshot could be resolved; the stable catalog title is shown.");
                }

                resolved[revision.Id] = snapshot;
                if (!string.IsNullOrWhiteSpace(snapshot.Title)) predecessorTitle = snapshot.Title;
            }
        }

        return requestedRows.ToDictionary(x => x.Id, x => resolved[x.Id]);
    }
}
