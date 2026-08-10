using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The immutable human-readable title belonging to one exact procedure revision.
///
/// Controlled revisions produced by a TCR take their title from the exact approved procedure-change snapshot
/// that produced them. A retirement always carries the nearest predecessor title because it withdraws that
/// procedure rather than authorizing a rename. Revisions that predate controlled TCR provenance receive a
/// deterministic, revision-specific compatibility label rather than today's mutable catalog title.
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
                                   }).ToListAsync(ct);
        var procedureIds = requestedRows.Select(x => x.ProcedureId).Distinct().ToList();

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
            TestProcedureRevisionTitleSnapshot? predecessor = null;
            foreach (var revision in chain.OrderBy(x => x.Revision))
            {
                TestProcedureRevisionTitleSnapshot snapshot;
                if (revision.SourceTestChangeRequestId is not Guid sourceTcrId)
                {
                    snapshot = new(revision.Id,
                        UnavailableTitle(revision.BaseNumber, revision.Revision, legacy: true),
                        false, true,
                        "Legacy revision — the exact historical title was not recorded on a controlled TCR snapshot; a deterministic compatibility label is shown.");
                }
                else if (changeByRevision.TryGetValue(
                             (sourceTcrId, revision.BaseNumber.ToUpperInvariant(), revision.Revision), out var change))
                {
                    if (change.Kind == TestProcedureChangeKind.Retire)
                    {
                        snapshot = predecessor is not null
                            ? new(revision.Id, predecessor.Title,
                                predecessor.IsExact, predecessor.IsLegacy,
                                predecessor.IsExact
                                    ? "Retirement revision — title preserved from the exact predecessor being retired."
                                    : "Retirement revision — the predecessor compatibility label is preserved because no exact historical title exists.")
                            : new(revision.Id,
                                UnavailableTitle(revision.BaseNumber, revision.Revision, legacy: false),
                                false, false,
                                "The TCR records a retirement, but no predecessor title can be resolved; supplied retirement text is not treated as exact.");
                    }
                    else if (!string.IsNullOrWhiteSpace(change.Title))
                    {
                        snapshot = new(revision.Id, change.Title.Trim(), true, false, null);
                    }
                    else
                    {
                        snapshot = new(revision.Id,
                            UnavailableTitle(revision.BaseNumber, revision.Revision, legacy: false),
                            false, false,
                            "The producing TCR is recorded, but its revision-title snapshot is unavailable; a deterministic revision label is shown.");
                    }
                }
                else
                {
                    snapshot = new(revision.Id,
                        UnavailableTitle(revision.BaseNumber, revision.Revision, legacy: false),
                        false, false,
                        "The producing TCR is recorded, but no matching procedure-change title snapshot could be resolved; a deterministic revision label is shown.");
                }

                resolved[revision.Id] = snapshot;
                predecessor = snapshot;
            }
        }

        return requestedRows.ToDictionary(x => x.Id, x => resolved[x.Id]);
    }

    /// <summary>
    /// Finds exact procedure revisions by the same authoritative title projection every reader displays.
    /// Raw TCR proposal text is deliberately not a search authority: retirement text is discarded in
    /// favour of the predecessor title, and legacy compatibility labels remain searchable as displayed.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> MatchingRevisionIdsAsync(
        AeroLinkDbContext db,
        IReadOnlyCollection<Guid> procedureRevisionIds,
        string query,
        CancellationToken ct)
    {
        var requested = procedureRevisionIds.Where(x => x != Guid.Empty).Distinct().ToList();
        var term = query?.Trim() ?? string.Empty;
        if (requested.Count == 0 || term.Length == 0) return [];
        var titles = await ForRevisionsAsync(db, requested, ct);
        return titles.Where(x => x.Value.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Key).OrderBy(x => x).ToList();
    }

    private static string UnavailableTitle(string baseNumber, int revision, bool legacy) =>
        $"{(legacy ? "Legacy procedure" : "Procedure")} {baseNumber}.{revision:D2} — " +
        (legacy ? "exact historical title was not recorded" : "exact revision title snapshot was not recorded");
}
