using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed class TraceWorkLimitException() : Exception("This trace exceeds the bounded read limit. Open a smaller build network or a more specific artifact.");

internal sealed class TraceReadBudget
{
    public const int MaximumNodes = 1000;
    public const int MaximumRows = 20000;
    private int _rows;
    private long _snapshotCharacters;
    public void ReserveSnapshotCharacters(long count)
    {
        _snapshotCharacters += count;
        if (_snapshotCharacters > 8_000_000) throw new TraceWorkLimitException();
    }
    public async Task<List<T>> ReadAsync<T>(IQueryable<T> query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rows = await query.Take(MaximumRows - _rows + 1).ToListAsync(ct);
        _rows += rows.Count;
        if (_rows > MaximumRows) throw new TraceWorkLimitException();
        return rows;
    }
}

public static partial class ChangeRequestTraceProjection
{
    private sealed class TraceScope
    {
        public HashSet<Guid> Changes { get; } = [];
        public HashSet<Guid> Reviews { get; } = [];
        public HashSet<Guid> Requirements { get; } = [];
        public HashSet<Guid> Code { get; } = [];
        public HashSet<Guid> Reports { get; } = [];
        public int Count => Changes.Count + Reviews.Count + Requirements.Count + Code.Count + Reports.Count;
        public bool Truncated { get; set; }
    }

    private static async Task<Guid[]> BoundedSnapshotIdsAsync(AeroLinkDbContext db, Guid projectId,
        IReadOnlyCollection<Guid> owners, TraceReadBudget budget, CancellationToken ct)
    {
        var headers = await budget.ReadAsync(from cycle in db.ReviewCycles.AsNoTracking()
            join owner in db.SystemChangeRequests.AsNoTracking() on cycle.ChangeRequestId equals owner.Id
            where owner.ProjectId == projectId && owners.Contains(owner.Id) && cycle.SnapshotContractVersion >= 3
            select new { cycle.Id, Characters = cycle.SnapshotJson.Length }, ct);
        budget.ReserveSnapshotCharacters(headers.Sum(x => (long)x.Characters));
        return headers.Select(x => x.Id).ToArray();
    }

    private static async Task<TraceScope> SelectBuildAsync(AeroLinkDbContext db, Guid projectId, Guid releaseId,
        int ceiling, TraceReadBudget budget, CancellationToken ct)
    {
        var scope = new TraceScope();
        var changes = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId && x.TargetReleaseId == releaseId);
        var reviews = db.TestChangeReviews.AsNoTracking().Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId);
        await SelectAsync(scope.Changes, changes.OrderBy(x => x.BaseNumber)
            .ThenBy(x => x.Revision < 10 ? "0" + x.Revision.ToString() : x.Revision.ToString()).ThenBy(x => x.Id).Select(x => x.Id));
        // Match the existing Kind/DisplayNumber/Id response order using one shared node budget.
        await SelectAsync(scope.Reports, db.ProblemReports.AsNoTracking().Where(report => report.ProjectId == projectId
            && db.ProblemReportLinks.Any(link => link.ProblemReportId == report.Id
                && ((link.ArtifactType == "ChangeRequest" && changes.Any(change => change.Id == link.ArtifactId))
                    || (link.ArtifactType == "TestChangeRequest" && reviews.Any(review => review.Id == link.ArtifactId)))))
            .OrderBy(x => x.ReportNumber).ThenBy(x => x.Revision < 10 ? "0" + x.Revision.ToString() : x.Revision.ToString()).ThenBy(x => x.Id)
            .Select(x => x.Id));
        await SelectAsync(scope.Reviews, reviews.OrderBy(x => x.BaseNumber)
            .ThenBy(x => x.Revision < 10 ? "0" + x.Revision.ToString() : x.Revision.ToString()).ThenBy(x => x.Id).Select(x => x.Id));
        async Task SelectAsync(HashSet<Guid> target, IQueryable<Guid> query)
        {
            var remaining = ceiling - scope.Count;
            var candidates = await budget.ReadAsync(query.Take(remaining + 1), ct);
            scope.Truncated |= candidates.Count > remaining;
            target.UnionWith(candidates.Take(remaining));
        }
        return scope;
    }

    private static async Task<TraceScope> DiscoverAsync(AeroLinkDbContext db, Guid projectId,
        Guid rootId, string rootKind, ILadderPolicy policy, TraceReadBudget budget, CancellationToken ct,
        bool directOnly = false)
    {
        var scope = new TraceScope();
        var changes = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId);
        var reviews = db.TestChangeReviews.AsNoTracking().Where(x => x.ProjectId == projectId);
        var requirements = from revision in db.RequirementRevisions.AsNoTracking()
                           join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                           where artifact.ProjectId == projectId select revision;
        if (rootKind == "ChangeRequest")
        {
            if (!await changes.AnyAsync(x => x.Id == rootId, ct)) return scope;
            scope.Changes.Add(rootId);
        }
        else
        {
            if (!await reviews.AnyAsync(x => x.Id == rootId, ct)) return scope;
            scope.Reviews.Add(rootId);
        }
        var expandedChanges = new HashSet<Guid>();
        var expandedReviews = new HashSet<Guid>();
        var expandedRequirements = new HashSet<Guid>();
        for (var depth = 0; ; depth++)
        {
            ct.ThrowIfCancellationRequested();
            var cr = scope.Changes.Except(expandedChanges).ToArray();
            var tcr = scope.Reviews.Except(expandedReviews).ToArray();
            var req = scope.Requirements.Except(expandedRequirements).ToArray();
            if (cr.Length + tcr.Length + req.Length == 0) return scope;
            if (depth >= 64) throw new TraceWorkLimitException();
            expandedChanges.UnionWith(cr); expandedReviews.UnionWith(tcr); expandedRequirements.UnionWith(req);
            if (cr.Length > 0)
            {
                var authored = await budget.ReadAsync(from link in db.ChangeRequestUpstreamLinks.AsNoTracking()
                    join child in changes on link.ChangeRequestId equals child.Id
                    join parent in changes on link.UpstreamChangeRequestId equals parent.Id
                    where cr.Contains(child.Id) || cr.Contains(parent.Id)
                    select new { Child = child.Id, Parent = parent.Id }, ct);
                foreach (var link in authored) { scope.Changes.Add(link.Child); scope.Changes.Add(link.Parent); }
                var frozen = await budget.ReadAsync(from link in db.Set<FrozenReviewTraceLink>().AsNoTracking()
                    join child in changes on link.OwnerId equals child.Id
                    join parent in changes on link.UpstreamId equals parent.Id
                    where link.ProjectId == projectId && (cr.Contains(link.OwnerId) || cr.Contains(link.UpstreamId))
                    select new { Child = child.Id, Parent = parent.Id }, ct);
                foreach (var link in frozen) { scope.Changes.Add(link.Child); scope.Changes.Add(link.Parent); }
                var assessed = await budget.ReadAsync(from assessment in db.DownstreamChangeAssessments.AsNoTracking()
                    join link in db.DownstreamAssessmentChangeRequestLinks.AsNoTracking() on assessment.Id equals link.AssessmentId
                    join child in changes on link.ChangeRequestId equals child.Id
                    join parent in changes on assessment.SourceChangeRequestId equals parent.Id
                    where assessment.ProjectId == projectId && assessment.State != DownstreamAssessmentState.Superseded
                        && (cr.Contains(child.Id) || cr.Contains(parent.Id))
                    select new { Child = child.Id, Parent = parent.Id, assessment.ReleaseId, assessment.TargetLevel, assessment.State,
                        ChildIdentity = new CrIdentity(child.ProjectId, child.TargetReleaseId, child.Type, child.SoftwareLevel, child.State),
                        ParentIdentity = new CrIdentity(parent.ProjectId, parent.TargetReleaseId, parent.Type, parent.SoftwareLevel, parent.State) }, ct);
                foreach (var link in assessed)
                    if (IsCurrentAssessmentEdge(projectId, link.State, link.ReleaseId, link.TargetLevel,
                        link.ParentIdentity, link.ChildIdentity, policy)) { scope.Changes.Add(link.Child); scope.Changes.Add(link.Parent); }
            }
            if (cr.Length + tcr.Length > 0)
            {
                var originQuery = from review in reviews
                    join change in changes on review.ChangeRequestId equals change.Id
                    where review.OriginKind == TestChangeReviewOriginKind.ChangeRequest
                    select new { Change = change.Id, Review = review.Id };
                // Separate indexed directions. An OR spanning both sides of a join can force PostgreSQL to
                // materialize every project CR/TCR before filtering, despite returning only one relation.
                var origins = await budget.ReadAsync(originQuery.Where(x => cr.Contains(x.Change))
                    .Concat(originQuery.Where(x => tcr.Contains(x.Review))), ct);
                var claimQuery = from claim in db.TestChangeRequestClaims.AsNoTracking()
                    join change in changes on claim.ChangeRequestId equals change.Id
                    join review in reviews on claim.TestChangeReviewId equals review.Id
                    select new { Change = change.Id, Review = review.Id };
                var claims = await budget.ReadAsync(claimQuery.Where(x => cr.Contains(x.Change))
                    .Concat(claimQuery.Where(x => tcr.Contains(x.Review))), ct);
                foreach (var pair in origins.Concat(claims)) { scope.Changes.Add(pair.Change); scope.Reviews.Add(pair.Review); }
            }
            if (tcr.Length > 0)
            {
                var direct = from procedure in reviews join owner in reviews on procedure.OriginReferenceId equals owner.Id
                    where procedure.ArtifactKind == VerificationArtifactKind.Procedure && procedure.OriginKind == TestChangeReviewOriginKind.CaseReview
                        && (tcr.Contains(procedure.Id) || tcr.Contains(owner.Id)) select new { Procedure = procedure.Id, Owner = owner.Id };
                var changed = from procedure in reviews join change in db.Set<TestProcedureChange>().AsNoTracking() on procedure.OriginReferenceId equals change.Id
                    join owner in reviews on change.TestChangeReviewId equals owner.Id
                    where procedure.ArtifactKind == VerificationArtifactKind.Procedure && procedure.OriginKind == TestChangeReviewOriginKind.CaseChange
                        && (tcr.Contains(procedure.Id) || tcr.Contains(owner.Id)) select new { Procedure = procedure.Id, Owner = owner.Id };
                var assessed = from procedure in reviews join assessment in db.VerificationImpactItems.AsNoTracking() on procedure.OriginReferenceId equals assessment.Id
                    join owner in reviews on assessment.TestChangeReviewId equals owner.Id
                    where procedure.ArtifactKind == VerificationArtifactKind.Procedure && procedure.OriginKind == TestChangeReviewOriginKind.CaseAssessment
                        && (tcr.Contains(procedure.Id) || tcr.Contains(owner.Id)) select new { Procedure = procedure.Id, Owner = owner.Id };
                foreach (var pair in await budget.ReadAsync(direct.Concat(changed).Concat(assessed), ct))
                { scope.Reviews.Add(pair.Procedure); scope.Reviews.Add(pair.Owner); }
            }
            if (cr.Length + req.Length > 0)
            {
                var owned = await budget.ReadAsync(from revision in requirements
                    join change in changes on revision.SourceChangeRequestId equals change.Id
                    where cr.Contains(change.Id) || req.Contains(revision.Id)
                    select new { Change = change.Id, Requirement = revision.Id }, ct);
                foreach (var pair in owned) { scope.Changes.Add(pair.Change); scope.Requirements.Add(pair.Requirement); }
            }
            if (req.Length > 0)
            {
                var traces = await budget.ReadAsync(from link in db.RequirementTraces.AsNoTracking()
                    join source in requirements on link.SourceRevisionId equals source.Id
                    join target in requirements on link.TargetRevisionId equals target.Id
                    where link.ProjectId == projectId && (req.Contains(source.Id) || req.Contains(target.Id))
                        && (link.ExactLinkSuspectLifecycleId == null || !db.ExactLinkSuspectLifecycles.Any(lifecycle =>
                            lifecycle.Id == link.ExactLinkSuspectLifecycleId && lifecycle.ProjectId == projectId
                            && lifecycle.LinkKind == ExactLinkKind.RequirementTrace && lifecycle.LinkId == link.Id
                            && lifecycle.State != ExactLinkLifecycleState.Closed))
                    select new { Source = source.Id, Target = target.Id }, ct);
                foreach (var pair in traces) { scope.Requirements.Add(pair.Source); scope.Requirements.Add(pair.Target); }
                scope.Code.UnionWith(await budget.ReadAsync(db.CodeTraceabilityRecords.AsNoTracking()
                    .Where(x => x.ProjectId == projectId && req.Contains(x.RequirementRevisionId)).Select(x => x.Id), ct));
            }
            if (scope.Count > TraceReadBudget.MaximumNodes) throw new TraceWorkLimitException();
            // The register inspector promises immediate relationships. Do not expand a neighbour's
            // requirements, siblings or review history into the whole connected component merely to
            // discard those extra hops in the browser. The same node/read budgets still apply.
            if (directOnly) return scope;
        }
    }
}
internal static class TraceReadExtensions
{
    internal static Task<List<T>> ReadTraceAsync<T>(this IQueryable<T> query, TraceReadBudget? budget, CancellationToken ct)
        => budget is null ? query.ToListAsync(ct) : budget.ReadAsync(query, ct);
}
