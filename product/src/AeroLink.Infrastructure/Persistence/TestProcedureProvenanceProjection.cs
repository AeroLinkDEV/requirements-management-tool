using System.Text.Json;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

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
/// Shared authority for Test Procedure Trace and History provenance. The producing TCR identity and
/// each exact source CR are revision-specific evidence; current movable TCR claims are compatibility
/// context only for revisions created before immutable source snapshots existed.
/// </summary>
public static class TestProcedureProvenanceProjection
{
    private sealed record ProcedureSourceSnapshot(
        Guid ChangeRequestId, string ChangeRequestNumber, bool Originating);

    public static async Task<IReadOnlyDictionary<Guid, TestProcedureRevisionProvenance>> ForRevisionsAsync(
        AeroLinkDbContext db,
        IReadOnlyCollection<Guid> procedureRevisionIds,
        CancellationToken ct)
    {
        var ids = procedureRevisionIds.Where(x => x != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, TestProcedureRevisionProvenance>();

        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.SourceTestChangeRequestId, x.SourceChangeRequestsJson })
            .ToListAsync(ct);
        var snapshots = revisions.ToDictionary(
            x => x.Id, x => ParseSources(x.SourceChangeRequestsJson));
        var impacts = await db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.ResolvedProcedureRevisionId != null
                && ids.Contains(x.ResolvedProcedureRevisionId.Value))
            .Select(x => new
            {
                RevisionId = x.ResolvedProcedureRevisionId!.Value,
                x.TestChangeReviewId,
                x.ChangeRequestId,
                x.SubjectDisplayNumber,
                x.ProcedureChangeAction,
            }).ToListAsync(ct);

        var reviewIds = revisions.Where(x => x.SourceTestChangeRequestId != null)
            .Select(x => x.SourceTestChangeRequestId!.Value)
            .Concat(impacts.Select(x => x.TestChangeReviewId))
            .Distinct().ToList();
        var reviews = await db.TestChangeReviews.AsNoTracking()
            .Where(x => reviewIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id, x.BaseNumber, x.Revision,
                x.SourceChangeRequestNumber, x.ChangeRequestId,
            }).ToListAsync(ct);
        var reviewById = reviews.ToDictionary(x => x.Id);
        var claims = await db.TestChangeRequestClaims.AsNoTracking()
            .Where(x => reviewIds.Contains(x.TestChangeReviewId))
            .Select(x => new
            {
                x.TestChangeReviewId, x.ChangeRequestId, x.ChangeRequestNumber,
            }).ToListAsync(ct);

        var changeIds = impacts.Select(x => x.ChangeRequestId)
            .Concat(reviews.Select(x => x.ChangeRequestId))
            .Concat(claims.Select(x => x.ChangeRequestId))
            .Concat(snapshots.Values.SelectMany(x => x).Select(x => x.ChangeRequestId))
            .Distinct().ToList();
        var changeNumbers = (await db.SystemChangeRequests.AsNoTracking()
                .Where(x => changeIds.Contains(x.Id))
                .Select(x => new { x.Id, x.BaseNumber, x.Revision })
                .ToListAsync(ct))
            .ToDictionary(x => x.Id, x => $"{x.BaseNumber}.{x.Revision:D2}");

        var result = new Dictionary<Guid, TestProcedureRevisionProvenance>();
        foreach (var revision in revisions)
        {
            var sourceReviewId = revision.SourceTestChangeRequestId;
            var sourceReview = sourceReviewId is Guid reviewId
                && reviewById.TryGetValue(reviewId, out var resolvedReview)
                    ? resolvedReview
                    : null;
            var package = sourceReview is not null
                ? Display(sourceReview.BaseNumber, sourceReview.Revision,
                    sourceReview.SourceChangeRequestNumber)
                : sourceReviewId is null ? null : $"Unresolved TCR {sourceReviewId}";
            var legacy = sourceReviewId is null;
            var drivers = new List<TestProcedureProvenanceDriver>();

            foreach (var impact in impacts.Where(x => x.RevisionId == revision.Id
                         && (sourceReviewId is null || x.TestChangeReviewId == sourceReviewId)))
            {
                var impactReview = reviewById.GetValueOrDefault(impact.TestChangeReviewId);
                var impactPackage = sourceReviewId is not null
                    ? package!
                    : impactReview is null
                        ? $"Unresolved TCR {impact.TestChangeReviewId}"
                        : Display(impactReview.BaseNumber, impactReview.Revision,
                            impactReview.SourceChangeRequestNumber);
                drivers.Add(new(
                    revision.Id, sourceReviewId, impactPackage,
                    ChangeNumber(impact.ChangeRequestId, changeNumbers),
                    impact.SubjectDisplayNumber,
                    impact.ProcedureChangeAction?.ToString() ?? "ImpactDecision",
                    legacy));
            }

            var compatibility = false;
            if (drivers.Count == 0 && sourceReview is not null)
            {
                IReadOnlyList<ProcedureSourceSnapshot> sources = snapshots[revision.Id];
                if (sources.Count == 0)
                {
                    compatibility = true;
                    sources = new[]
                        {
                            new ProcedureSourceSnapshot(sourceReview.ChangeRequestId,
                                sourceReview.SourceChangeRequestNumber, true),
                        }
                        .Concat(claims.Where(x => x.TestChangeReviewId == sourceReview.Id)
                            .Select(x => new ProcedureSourceSnapshot(
                                x.ChangeRequestId, x.ChangeRequestNumber, false)))
                        .DistinctBy(x => x.ChangeRequestId)
                        .ToList();
                }

                foreach (var source in sources)
                    drivers.Add(new(
                        revision.Id, sourceReviewId, package!,
                        changeNumbers.TryGetValue(source.ChangeRequestId, out var exact)
                            ? exact : source.ChangeRequestNumber,
                        "", "PackageSource", false));
            }

            drivers = drivers.Distinct().OrderBy(x => x.Package)
                .ThenBy(x => x.ChangeRequest).ThenBy(x => x.SubjectDisplayNumber).ToList();
            var note = legacy
                ? "Legacy revision — the producing test change request was not recorded."
                : sourceReview is null
                    ? $"The recorded producing test change request {sourceReviewId} could not be resolved."
                    : compatibility
                        ? "This revision predates immutable package-source snapshots; surviving package claims are compatibility context."
                        : null;
            result[revision.Id] = new(
                revision.Id, sourceReviewId, package, legacy, note, drivers);
        }

        return result;
    }

    private static IReadOnlyList<ProcedureSourceSnapshot> ParseSources(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]") return [];
        try
        {
            return (JsonSerializer.Deserialize<List<ProcedureSourceSnapshot>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [])
                .Where(x => x.ChangeRequestId != Guid.Empty
                    && !string.IsNullOrWhiteSpace(x.ChangeRequestNumber))
                .DistinctBy(x => x.ChangeRequestId).OrderBy(x => x.ChangeRequestId).ToList();
        }
        catch (JsonException) { return []; }
    }

    private static string Display(string baseNumber, int revision, string legacyNumber) =>
        string.IsNullOrWhiteSpace(baseNumber) ? legacyNumber : $"{baseNumber}.{revision:D2}";

    private static string ChangeNumber(Guid id, IReadOnlyDictionary<Guid, string> numbers) =>
        numbers.TryGetValue(id, out var number) ? number : $"Unresolved change request {id}";
}
