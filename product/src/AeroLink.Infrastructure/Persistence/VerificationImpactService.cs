using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// One requirement change, as it looked once a baseline materialised it into an exact revision.
/// <paramref name="PriorRevisionId"/> is the revision this one supersedes, absent for an introduction.
/// </summary>
public sealed record MaterializedRequirementChange(
    Guid ChangeRequestId,
    Guid RequirementChangeId,
    RequirementChangeKind Kind,
    Guid? PriorRevisionId,
    Guid RevisionId,
    string DisplayNumber);

/// <summary>What materialisation settled: items bound to revisions, coverage carried forward and confirmed,
/// and procedures a retirement left covering nothing.</summary>
public sealed record MaterializationImpactResult(int BoundToRevision, int CoverageCarriedForward,
    int CoverageConfirmed, int ProceduresOrphaned);
public sealed record ApprovedProcedureSelection(Guid ProcedureId, Guid RevisionId, int Revision,
    string DisplayNumber, string Title, string Level, string State);

/// <summary>
/// Raises verification work when an approved change alters what must be tested, and keeps that work
/// attached to the release that will carry the change.
///
/// Items are raised at change-request approval rather than at baseline inclusion so verification can start
/// as soon as the engineering decision is settled, rather than discovering the work when the release is
/// already being assembled.
/// </summary>
public sealed class VerificationImpactService(AeroLinkDbContext db)
{
    /// <summary>
    /// Raises the items owed by a newly approved change request. Safe to call more than once for the same
    /// change request: existing items for the same requirement change are left alone, so a retried approval
    /// never duplicates work.
    /// </summary>
    public async Task<int> RaiseForApprovedChangeRequestAsync(SystemChangeRequest request, DateTimeOffset now, CancellationToken ct)
    {
        // Selecting an approved change into a candidate baseline moves it to SelectedForBaseline, so both
        // states mean "approved". Testing only for Approved would make a retried raise silently do nothing
        // once the change had been selected.
        if (request.State is not (ScrState.Approved or ScrState.SelectedForBaseline)) return 0;

        var alreadyRaised = await db.VerificationImpactItems
            .Where(x => x.ChangeRequestId == request.Id)
            .Select(x => x.RequirementChangeId)
            .ToListAsync(ct);
        var covered = alreadyRaised.Where(x => x is not null).Select(x => x!.Value).ToHashSet();
        var reviews = await db.TestChangeReviews
            .Where(x => x.ChangeRequestId == request.Id)
            .ToDictionaryAsync(x => x.Discipline, ct);

        var raised = 0;
        foreach (var change in request.RequirementChanges)
        {
            if (covered.Contains(change.Id)) continue;
            if (change.Kind is not (RequirementChangeKind.Introduce or RequirementChangeKind.Modify)) continue;
            var discipline = Discipline(change.Level);
            if (!reviews.TryGetValue(discipline, out var review))
            {
                review = new TestChangeReview(request.ProjectId, request.TargetReleaseId, request.Id,
                    discipline, request.DisplayNumber, now);
                db.TestChangeReviews.Add(review);
                reviews.Add(discipline, review);
            }
            var display = ArtifactNumberDisplay(change);
            VerificationImpactItem? item = change.Kind switch
            {
                RequirementChangeKind.Introduce => VerificationImpactItem.ForIntroducedRequirement(
                    request.ProjectId, request.TargetReleaseId, request.Id, review.Id, change.Id, display, change.VerificationMethod, now),
                RequirementChangeKind.Modify => VerificationImpactItem.ForModifiedRequirement(
                    request.ProjectId, request.TargetReleaseId, request.Id, review.Id, change.Id, display, change.VerificationMethod, now),
                // Retirement raises work only where it strands a procedure, which is resolved separately
                // once the retirement is materialised and remaining links are known.
                _ => null
            };
            if (item is null) continue;
            db.VerificationImpactItems.Add(item);
            raised++;
        }

        return raised;
    }

    /// <summary>
    /// Moves outstanding verification work with its change request when the target release changes, so the
    /// work is never stranded against a release the change no longer belongs to.
    /// </summary>
    public async Task<int> RetargetAsync(Guid changeRequestId, Guid releaseId, DateTimeOffset now, CancellationToken ct)
    {
        var items = await db.VerificationImpactItems.Where(x => x.ChangeRequestId == changeRequestId).ToListAsync(ct);
        foreach (var item in items) item.Retarget(releaseId, now);
        var reviews = await db.TestChangeReviews.Where(x => x.ChangeRequestId == changeRequestId).ToListAsync(ct);
        foreach (var review in reviews) review.Retarget(releaseId, now);
        return items.Count;
    }

    /// <summary>
    /// Completes the loop at materialisation, which is the first moment requirement revisions exist.
    ///
    /// Three things become possible only here. Items anchored to an approved requirement change bind to the
    /// exact revision that change produced. Coverage on a modified requirement carries forward onto the new
    /// revision marked suspect, because the procedure was written against the previous wording and nobody has
    /// yet said it still holds. A procedure left covering nothing by a retirement raises its own item.
    ///
    /// Runs inside the materialisation transaction and does not save; the caller owns the unit of work.
    /// </summary>
    public async Task<MaterializationImpactResult> ApplyMaterializationAsync(Guid projectId, Guid releaseId,
        IReadOnlyList<MaterializedRequirementChange> changes, DateTimeOffset now, CancellationToken ct)
    {
        if (changes.Count == 0) return new MaterializationImpactResult(0, 0, 0, 0);

        var changeIds = changes.Select(x => x.RequirementChangeId).ToList();
        var items = await db.VerificationImpactItems
            .Where(x => x.RequirementChangeId != null && changeIds.Contains(x.RequirementChangeId!.Value))
            .ToListAsync(ct);
        var itemByChange = items
            .GroupBy(x => x.RequirementChangeId!.Value)
            .ToDictionary(x => x.Key, x => x.First());

        var bound = 0;
        foreach (var change in changes)
        {
            if (!itemByChange.TryGetValue(change.RequirementChangeId, out var item)) continue;
            if (item.Trigger == VerificationImpactTrigger.ProcedureOrphaned) continue;
            item.LinkRequirementRevision(change.RevisionId, now);
            bound++;
        }

        var carried = await CarryCoverageForwardAsync(changes, now, ct);
        var confirmed = await ConfirmDecidedCoverageAsync(changes, itemByChange, carried, now, ct);
        var orphaned = await RaiseOrphanedProceduresAsync(projectId, releaseId, changes, carried, now, ct);
        return new MaterializationImpactResult(bound, carried.Count, confirmed, orphaned);
    }

    /// <summary>
    /// Copies every coverage link on the previous revision of a modified requirement onto the new revision,
    /// marked suspect. Without this the new revision would silently have no coverage at all, which reads as
    /// "nothing to verify" rather than "verification needs rechecking".
    /// </summary>
    private async Task<List<TestRequirementCoverage>> CarryCoverageForwardAsync(
        IReadOnlyList<MaterializedRequirementChange> changes, DateTimeOffset now, CancellationToken ct)
    {
        var modified = changes
            .Where(x => x.Kind == RequirementChangeKind.Modify && x.PriorRevisionId is not null)
            .ToList();
        if (modified.Count == 0) return [];

        var priorIds = modified.Select(x => x.PriorRevisionId!.Value).Distinct().ToList();
        var priorCoverage = await db.TestCoverage.AsNoTracking()
            .Where(x => priorIds.Contains(x.RequirementRevisionId))
            .ToListAsync(ct);
        if (priorCoverage.Count == 0) return [];

        var carried = new List<TestRequirementCoverage>();
        foreach (var change in modified)
        {
            foreach (var coverage in priorCoverage.Where(x => x.RequirementRevisionId == change.PriorRevisionId!.Value))
            {
                var link = TestRequirementCoverage.CarriedForward(coverage.ProcedureRevisionId, change.RevisionId,
                    $"{change.DisplayNumber} changed under this procedure, which was written against the previous wording.", now);
                db.TestCoverage.Add(link);
                carried.Add(link);
            }
        }
        return carried;
    }

    /// <summary>
    /// Turns a resolved "this procedure covers it" decision into the exact link it always meant. A
    /// carried-forward link to the same procedure is confirmed rather than duplicated, so the decision
    /// clears the suspect flag it was made about.
    /// </summary>
    private async Task<int> ConfirmDecidedCoverageAsync(IReadOnlyList<MaterializedRequirementChange> changes,
        IReadOnlyDictionary<Guid, VerificationImpactItem> itemByChange,
        List<TestRequirementCoverage> carried, DateTimeOffset now, CancellationToken ct)
    {
        var decided = changes
            .Select(change => itemByChange.TryGetValue(change.RequirementChangeId, out var item) ? (change, item) : default)
            .Where(x => x.item is not null
                && x.item.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed
                && x.item.ResolvedProcedureId is not null)
            .ToList();
        if (decided.Count == 0) return 0;

        // The named procedure's approved revision is what a link may point at; the endpoint already refused
        // to record the decision against a procedure without one. Procedures pre-date materialisation, so a
        // plain query is enough — nothing here was created in this transaction.
        var procedureIds = decided.Select(x => x.item.ResolvedProcedureId!.Value).Distinct().ToList();
        var approvedRevisions = (await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => procedureIds.Contains(x.ProcedureId) && x.State == TestProcedureState.Approved)
                .Select(x => new { x.Id, x.ProcedureId, x.Revision })
                .ToListAsync(ct))
            .GroupBy(x => x.ProcedureId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.Revision).First().Id);

        var confirmed = 0;
        foreach (var (change, item) in decided)
        {
            var procedureRevisionId = item.ResolvedProcedureRevisionId;
            if (procedureRevisionId is null
                && approvedRevisions.TryGetValue(item.ResolvedProcedureId!.Value, out var legacyRevisionId))
                procedureRevisionId = legacyRevisionId;
            if (procedureRevisionId is null) continue;
            var existing = carried.SingleOrDefault(x =>
                x.RequirementRevisionId == change.RevisionId && x.ProcedureRevisionId == procedureRevisionId.Value);
            if (existing is not null)
            {
                existing.ConfirmStillValid(item.ResolvedBy ?? "verification", now);
            }
            else
            {
                db.TestCoverage.Add(new TestRequirementCoverage(procedureRevisionId.Value, change.RevisionId));
            }
            confirmed++;
        }
        return confirmed;
    }

    /// <summary>
    /// Raises an item for a procedure a retirement left covering nothing.
    ///
    /// Only procedures touched by this materialisation's retirements are considered, and only those with no
    /// remaining link to an active requirement revision. A procedure that still covers something else stays
    /// quiet, and a newly authored procedure that has never been linked is not an orphan — it is unfinished
    /// work, which is a different thing and not this signal's business.
    /// </summary>
    private async Task<int> RaiseOrphanedProceduresAsync(Guid projectId, Guid releaseId,
        IReadOnlyList<MaterializedRequirementChange> changes, List<TestRequirementCoverage> carried,
        DateTimeOffset now, CancellationToken ct)
    {
        var retired = changes
            .Where(x => x.Kind == RequirementChangeKind.Retire && x.PriorRevisionId is not null)
            .ToList();
        if (retired.Count == 0) return 0;

        var retiredRevisionIds = retired.Select(x => x.PriorRevisionId!.Value).Distinct().ToHashSet();
        var strandedProcedureRevisions = await db.TestCoverage.AsNoTracking()
            .Where(x => retiredRevisionIds.Contains(x.RequirementRevisionId))
            .Select(x => x.ProcedureRevisionId)
            .Distinct()
            .ToListAsync(ct);
        if (strandedProcedureRevisions.Count == 0) return 0;

        // Coverage that survives: a link from the same procedure revision to a requirement revision that is
        // still active. Links added earlier in this same transaction count, hence the pending set.
        var survivingLinks = await db.TestCoverage.AsNoTracking()
            .Where(x => strandedProcedureRevisions.Contains(x.ProcedureRevisionId)
                && !retiredRevisionIds.Contains(x.RequirementRevisionId))
            .Join(db.RequirementRevisions.AsNoTracking().Where(r => r.State == RequirementRevisionState.Active),
                coverage => coverage.RequirementRevisionId, revision => revision.Id,
                (coverage, _) => coverage.ProcedureRevisionId)
            .Distinct()
            .ToListAsync(ct);
        var stillCovering = survivingLinks
            .Concat(carried.Select(x => x.ProcedureRevisionId))
            .ToHashSet();

        var orphanedRevisionIds = strandedProcedureRevisions.Where(x => !stillCovering.Contains(x)).ToList();
        if (orphanedRevisionIds.Count == 0) return 0;

        var orphanedProcedures = await db.TestProcedureRevisions.AsNoTracking()
            .Where(revision => orphanedRevisionIds.Contains(revision.Id))
            .Join(db.TestProcedures.AsNoTracking().Where(p => p.ProjectId == projectId),
                revision => revision.ProcedureId, procedure => procedure.Id,
                (revision, procedure) => new { procedure.Id, procedure.BaseNumber, procedure.Level })
            .Distinct()
            .ToListAsync(ct);
        if (orphanedProcedures.Count == 0) return 0;

        var alreadyRaised = await db.VerificationImpactItems
            .Where(x => x.Trigger == VerificationImpactTrigger.ProcedureOrphaned && x.State != VerificationImpactState.Resolved)
            .Select(x => x.ProcedureId).ToListAsync(ct);
        var covered = alreadyRaised.Where(x => x is not null).Select(x => x!.Value).ToHashSet();
        var changeRequestId = retired[0].ChangeRequestId;
        var sourceNumber = await db.SystemChangeRequests.Where(x => x.Id == changeRequestId)
            .Select(x => x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision)
            .SingleAsync(ct);
        var reviews = await db.TestChangeReviews.Where(x => x.ChangeRequestId == changeRequestId)
            .ToDictionaryAsync(x => x.Discipline, ct);

        var raised = 0;
        foreach (var procedure in orphanedProcedures)
        {
            if (!covered.Add(procedure.Id)) continue;
            var discipline = procedure.Level switch
            {
                TestProcedureLevel.System => TestChangeReviewDiscipline.System,
                TestProcedureLevel.HighLevel => TestChangeReviewDiscipline.HighLevelSoftware,
                _ => TestChangeReviewDiscipline.LowLevelSoftware
            };
            if (!reviews.TryGetValue(discipline, out var review))
            {
                review = new TestChangeReview(projectId, releaseId, changeRequestId, discipline, sourceNumber, now);
                db.TestChangeReviews.Add(review);
                reviews.Add(discipline, review);
            }
            db.VerificationImpactItems.Add(VerificationImpactItem.ForOrphanedProcedure(
                projectId, releaseId, changeRequestId, review.Id, procedure.Id, procedure.BaseNumber, now));
            raised++;
        }
        return raised;
    }

    /// <summary>Unresolved items are what hold a baseline back from approval.</summary>
    public Task<List<VerificationImpactItem>> OutstandingForReleaseAsync(Guid releaseId, CancellationToken ct)
        => db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.ReleaseId == releaseId && x.State != VerificationImpactState.Resolved)
            .OrderBy(x => x.Trigger).ThenBy(x => x.SubjectDisplayNumber)
            .ToListAsync(ct);

    public Task<List<VerificationImpactItem>> ForReleaseAsync(Guid releaseId, CancellationToken ct)
        => db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.ReleaseId == releaseId)
            .OrderBy(x => x.State).ThenBy(x => x.Trigger).ThenBy(x => x.SubjectDisplayNumber)
            .ToListAsync(ct);

    // The release-approval rule lives in exactly one place: the verification_impact gate in
    // ReleaseReadinessService, which /api/release-campaigns/{id}/release refuses to proceed without. A second
    // direct check here would be a second copy of the same rule to keep in step, so there isn't one.

    /// <summary>
    /// Confirms the named procedure exists in the Project and has an approved revision. Coverage may only be
    /// claimed against a procedure that is actually approved.
    /// </summary>
    public async Task<bool> HasApprovedProcedureAsync(Guid projectId, Guid procedureId, CancellationToken ct)
        => await FindApprovedProcedureAsync(projectId, procedureId, ct) is not null;

    public async Task<ApprovedProcedureSelection?> FindApprovedProcedureAsync(
        Guid projectId, Guid procedureId, CancellationToken ct)
    {
        var revisions = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                               where revision.ProcedureId == procedureId && revision.State == TestProcedureState.Approved
                               join procedure in db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId)
                                   on revision.ProcedureId equals procedure.Id
                               select new
                               {
                                   procedure.Id,
                                   RevisionId = revision.Id,
                                   revision.Revision,
                                   procedure.BaseNumber,
                                   procedure.Title,
                                   Level = procedure.Level.ToString(),
                                   State = revision.State.ToString()
                               }).ToListAsync(ct);
        var selected = revisions.OrderByDescending(x => x.Revision).FirstOrDefault();
        return selected is null ? null : new ApprovedProcedureSelection(
            selected.Id, selected.RevisionId, selected.Revision,
            $"{selected.BaseNumber}.{selected.Revision:D2}", selected.Title, selected.Level, selected.State);
    }

    /// <summary>
    /// Applies a coverage-confirmed decision immediately when materialisation has already bound the item to
    /// an exact requirement revision. Before materialisation there is nothing to link, so the same decision is
    /// applied later by <see cref="ApplyMaterializationAsync"/>.
    /// </summary>
    public async Task<bool> ApplyResolvedCoverageAsync(VerificationImpactItem item, DateTimeOffset now, CancellationToken ct)
    {
        if (item.Outcome != VerificationImpactOutcome.ProcedureCoverageConfirmed
            || item.ResolvedProcedureId is null
            || item.RequirementRevisionId is null)
            return false;

        var procedureRevisionId = item.ResolvedProcedureRevisionId;
        if (procedureRevisionId is null)
            procedureRevisionId = await db.TestProcedureRevisions
                .Where(x => x.ProcedureId == item.ResolvedProcedureId.Value && x.State == TestProcedureState.Approved)
                .OrderByDescending(x => x.Revision)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(ct);
        if (procedureRevisionId is null) return false;

        var existing = await db.TestCoverage.SingleOrDefaultAsync(
            x => x.RequirementRevisionId == item.RequirementRevisionId.Value
                && x.ProcedureRevisionId == procedureRevisionId.Value, ct);
        if (existing is not null)
        {
            existing.ConfirmStillValid(item.ResolvedBy ?? "verification", now);
        }
        else
        {
            db.TestCoverage.Add(new TestRequirementCoverage(
                procedureRevisionId.Value, item.RequirementRevisionId.Value));
        }
        return true;
    }

    public async Task<bool> ReopenResolvedCoverageAsync(VerificationImpactItem item, string rationale,
        DateTimeOffset now, CancellationToken ct)
    {
        if (item.Outcome != VerificationImpactOutcome.ProcedureCoverageConfirmed
            || item.RequirementRevisionId is null
            || item.ResolvedProcedureRevisionId is null)
            return false;
        var existing = await db.TestCoverage.SingleOrDefaultAsync(
            x => x.RequirementRevisionId == item.RequirementRevisionId.Value
                && x.ProcedureRevisionId == item.ResolvedProcedureRevisionId.Value, ct);
          if (existing is null) return false;
          var reason = $"Verification-impact decision reopened: {rationale}";
          existing.MarkSuspect(reason.Length <= 500 ? reason : reason[..500], now);
          return true;
      }

    private static string ArtifactNumberDisplay(RequirementChange change) =>
        $"{change.BaseNumber}.{change.Revision:00}";

    private static TestChangeReviewDiscipline Discipline(RequirementLevel level) => level switch
    {
        RequirementLevel.System => TestChangeReviewDiscipline.System,
        RequirementLevel.HighLevel => TestChangeReviewDiscipline.HighLevelSoftware,
        _ => TestChangeReviewDiscipline.LowLevelSoftware
    };
}
