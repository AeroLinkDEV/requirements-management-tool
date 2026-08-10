using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// One exact reason a controlled procedure revision exists.
///
/// Package is the producing TCR revision, ChangeRequest is the source engineering change represented by this
/// row, and Subject/Action are the impact detail when one exists. Manual TCRs legitimately have package/source
/// provenance without an impact item, in which case Subject is empty and Action is PackageSource.
/// </summary>
public sealed record TestProcedureProvenanceDriver(
    Guid ProcedureRevisionId,
    Guid? SourceTestChangeRequestId,
    string Package,
    string ChangeRequest,
    string SubjectDisplayNumber,
    string Action,
    bool IsLegacy);

public sealed record TestProcedureRevisionProvenance(
    Guid ProcedureRevisionId,
    Guid? SourceTestChangeRequestId,
    string? Package,
    bool IsLegacy,
    string? Note,
    IReadOnlyList<TestProcedureProvenanceDriver> Drivers);

/// <summary>
/// Shared authority for Test Procedure Trace and History provenance.
///
/// The immutable TestProcedureRevision.SourceTestChangeRequestId is the primary producing-package link. Impact
/// items add exact source-CR/subject/action context, using each item's own ChangeRequestId so folded work keeps
/// its identity. When a manual TCR has no resolving impact item, the package's primary and claimed CR sources
/// remain truthful provenance. Legacy revisions never acquire an invented TCR.
/// </summary>
public static class TestProcedureProvenanceProjection
{
    private const string LegacyPackage = "Legacy — producing TCR not recorded";

    public static async Task<IReadOnlyDictionary<Guid, TestProcedureRevisionProvenance>> ForRevisionsAsync(
        AeroLinkDbContext db,
        IReadOnlyCollection<Guid> procedureRevisionIds,
        CancellationToken ct)
    {
        var ids = procedureRevisionIds.Where(x => x != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, TestProcedureRevisionProvenance>();

        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.SourceTestChangeRequestId })
            .ToListAsync(ct);
        var reviewIds = revisions.Where(x => x.SourceTestChangeRequestId is not null)
            .Select(x => x.SourceTestChangeRequestId!.Value).Distinct().ToList();
        var reviews = await db.TestChangeReviews.AsNoTracking()
            .Where(x => reviewIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                x.BaseNumber,
                x.Revision,
                x.SourceChangeRequestNumber,
                x.ChangeRequestId,
            }).ToListAsync(ct);
        var reviewById = reviews.ToDictionary(x => x.Id);

        var claims = await db.TestChangeRequestClaims.AsNoTracking()
            .Where(x => reviewIds.Contains(x.TestChangeReviewId))
            .Select(x => new
            {
                x.TestChangeReviewId,
                x.ChangeRequestId,
                x.ChangeRequestNumber,
            }).ToListAsync(ct);

        var impacts = await db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.ResolvedProcedureRevisionId != null
                && ids.Contains(x.ResolvedProcedureRevisionId.Value))
            .Select(x => new
            {
                RevisionId = x.ResolvedProcedureRevisionId!.Value,
                x.ChangeRequestId,
                x.SubjectDisplayNumber,
                x.ProcedureChangeAction,
            }).ToListAsync(ct);

        var changeIds = impacts.Select(x => x.ChangeRequestId)
            .Concat(reviews.Select(x => x.ChangeRequestId))
            .Concat(claims.Select(x => x.ChangeRequestId))
            .Distinct().ToList();
        var changes = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => changeIds.Contains(x.Id))
            .Select(x => new { x.Id, x.BaseNumber, x.Revision })
            .ToListAsync(ct);
        var changeNumbers = changes.ToDictionary(x => x.Id,
            x => $"{x.BaseNumber}.{x.Revision:D2}");

        var result = new Dictionary<Guid, TestProcedureRevisionProvenance>();
        foreach (var revision in revisions)
        {
            var sourceReviewId = revision.SourceTestChangeRequestId;
            var hasReview = sourceReviewId is Guid reviewId && reviewById.TryGetValue(reviewId, out var review);
            var package = hasReview
                ? Display(review!.BaseNumber, review.Revision, review.SourceChangeRequestNumber)
                : sourceReviewId is null ? null : $"Unresolved TCR {sourceReviewId}";
            var legacy = sourceReviewId is null;
            var rows = new List<TestProcedureProvenanceDriver>();

            foreach (var impact in impacts.Where(x => x.RevisionId == revision.Id))
            {
                rows.Add(new(
                    revision.Id,
                    sourceReviewId,
                    package ?? LegacyPackage,
                    ChangeNumber(impact.ChangeRequestId, changeNumbers),
                    impact.SubjectDisplayNumber,
                    impact.ProcedureChangeAction?.ToString() ?? "ImpactDecision",
                    legacy));
            }

            // Manual first-class TCRs can produce a revision without any resolving impact item. The package
            // still owns exact primary/additional CR claims, so expose those instead of showing no provenance.
            if (rows.Count == 0 && hasReview)
            {
                var sources = new List<(Guid Id, string Fallback)>
                {
                    (review!.ChangeRequestId, review.SourceChangeRequestNumber),
                };
                sources.AddRange(claims.Where(x => x.TestChangeReviewId == review.Id)
                    .Select(x => (x.ChangeRequestId, x.ChangeRequestNumber)));
                foreach (var source in sources.DistinctBy(x => x.Id))
                {
                    rows.Add(new(
                        revision.Id,
                        sourceReviewId,
                        package!,
                        changeNumbers.TryGetValue(source.Id, out var exact) ? exact : source.Fallback,
                        "",
                        "PackageSource",
                        false));
                }
            }

            rows = rows.Distinct().OrderBy(x => x.Package).ThenBy(x => x.ChangeRequest)
                .ThenBy(x => x.SubjectDisplayNumber).ToList();
            var note = legacy
                ? "Legacy revision — the producing test change request was not recorded."
                : !hasReview
                    ? $"The recorded producing test change request {sourceReviewId} could not be resolved."
                    : null;
            result[revision.Id] = new(
                revision.Id,
                sourceReviewId,
                package,
                legacy,
                note,
                rows);
        }

        return result;
    }

    private static string Display(string baseNumber, int revision, string legacyNumber) =>
        string.IsNullOrWhiteSpace(baseNumber) ? legacyNumber : $"{baseNumber}.{revision:D2}";

    private static string ChangeNumber(Guid id, IReadOnlyDictionary<Guid, string> numbers) =>
        numbers.TryGetValue(id, out var number) ? number : $"Unresolved change request {id}";
}
