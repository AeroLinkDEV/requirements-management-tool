using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
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
    string DisplayNumber, string Title, bool TitleIsExact, bool TitleIsLegacy, string? TitleNote,
    string Level, string State);

/// <summary>
/// Raises verification work when an approved change alters what must be tested, and keeps that work
/// attached to the release that will carry the change.
///
/// Items are raised at change-request approval rather than at baseline inclusion so verification can start
/// as soon as the engineering decision is settled, rather than discovering the work when the release is
/// already being assembled.
/// </summary>
public sealed class VerificationImpactService(AeroLinkDbContext db, ProblemReportLinkService? problemReports = null,
    ILadderPolicy? policy = null, IProjectLadderPolicyResolver? policyResolver = null)
{
    private readonly ILadderPolicy fallbackPolicy = policy ?? LegacyLadderPolicy.Instance;
    /// <summary>
    /// Raises the items owed by a newly approved change request. Safe to call more than once for the same
    /// change request: existing items for the same requirement change are left alone, so a retried approval
    /// never duplicates work.
    /// </summary>
    public async Task<int> RaiseForApprovedChangeRequestAsync(SystemChangeRequest request, DateTimeOffset now,
        CancellationToken ct, string? actionActor = null)
    {
        var ladderPolicy = policyResolver is null
            ? fallbackPolicy
            : await policyResolver.ResolveAsync(request.ProjectId, ct);
        // Selecting an approved change into a candidate baseline moves it to SelectedForBaseline, so both
        // states mean "approved". Testing only for Approved would make a retried raise silently do nothing
        // once the change had been selected.
        if (request.State is not (ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline)) return 0;

        var alreadyRaised = await db.VerificationImpactItems
            .Where(x => x.ChangeRequestId == request.Id)
            .Select(x => x.RequirementChangeId)
            .ToListAsync(ct);
        var covered = alreadyRaised.Where(x => x is not null).Select(x => x!.Value).ToHashSet();
        var reviews = await db.TestChangeReviews
            .Where(x => x.ChangeRequestId == request.Id)
            .ToDictionaryAsync(x => x.Discipline, ct);
        var priorRequestIds = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => x.ProjectId == request.ProjectId && x.BaseNumber == request.BaseNumber && x.Revision < request.Revision)
            .Select(x => x.Id).ToListAsync(ct);
        var priorReviews = await db.TestChangeReviews
            // Prior packages raised from an earlier revision of this change request. A package raised from a
            // Problem Report is not a prior review of any change request, so it is not one of these.
            .Where(x => x.ChangeRequestId != null && priorRequestIds.Contains(x.ChangeRequestId.Value)
                && x.State != TestChangeReviewState.Superseded)
            .ToListAsync(ct);
        var priorReviewIds = priorReviews.Select(x => x.Id).ToList();
        var priorItems = await db.VerificationImpactItems
            .Where(x => priorReviewIds.Contains(x.TestChangeReviewId)).ToListAsync(ct);

        var raised = 0;
        foreach (var change in request.RequirementChanges)
        {
            if (covered.Contains(change.Id)) continue;
            if (change.Kind is not (RequirementChangeKind.Introduce or RequirementChangeKind.Modify)) continue;
            // A present requirement level may deliberately omit verification. Such a change has no
            // procedure discipline to route and must not manufacture a review for an absent capability.
            if (!ladderPolicy.OrderedLevels.Contains(change.Level)) continue;
            if (ladderPolicy.Definition(change.Level).Verification is null) continue;
            var discipline = ladderPolicy.Discipline(change.Level);
            if (!reviews.TryGetValue(discipline, out var review))
            {
                // Raised unnumbered: an approved change needs assessing, and only an assessment that finds
                // test-procedure work turns this into a controlled test change request. Numbering here gave
                // every approved change a SYSTPCR before anybody had looked at whether it touched a procedure.
                review = new TestChangeReview(request.ProjectId, request.TargetReleaseId, request.Id,
                    discipline, request.DisplayNumber, now);
                db.TestChangeReviews.Add(review);
                await (problemReports ?? new ProblemReportLinkService(db)).PropagateToTestChangeRequestAsync(
                    request.Id, review.Id, actionActor ?? request.AuthorId, now, ct);
                reviews.Add(discipline, review);
                foreach (var historical in priorReviews.Where(x => x.Discipline == discipline))
                {
                    historical.Supersede(review.Id,
                        $"{request.DisplayNumber} supersedes the source revision. Its verification decisions require an updated test change request.", now);
                    foreach (var historicalItem in priorItems.Where(x => x.TestChangeReviewId == historical.Id)) historicalItem.Supersede(now);
                }
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
    /// Extends the existing unnumbered assessment chain by one configured artifact step. Each approved Case
    /// review receives one same-discipline Procedure assessment only when the effective profile contains
    /// Procedure. The shared conclusion route later decides whether that assessment disappears as no-change
    /// evidence or becomes a numbered HLRTPCR/LLRTPCR package.
    /// </summary>
    public async Task<int> RaiseForApprovedCaseReviewAsync(TestChangeReview caseReview, DateTimeOffset now,
        CancellationToken ct)
    {
        if (caseReview.State != TestChangeReviewState.Approved
            || caseReview.ArtifactKind != VerificationArtifactKind.Case)
            return 0;

        var ladderPolicy = policyResolver is null
            ? fallbackPolicy
            : await policyResolver.ResolveAsync(caseReview.ProjectId, ct);
        var level = ladderPolicy.RequirementLevelFor(caseReview.Discipline);
        var profile = ladderPolicy.VerificationProfile(level);
        if (!profile.Enables(VerificationArtifactKind.Procedure)) return 0;
        var procedureKey = new VerificationArtifactKey(profile.Discipline, VerificationArtifactKind.Procedure);
        _ = ladderPolicy.VerificationArtifact(procedureKey); // typed, fail-closed effective-profile authority

        var changes = await db.Set<TestProcedureChange>().AsNoTracking()
            .Where(x => x.TestChangeReviewId == caseReview.Id)
            .OrderBy(x => x.BaseNumber).ThenBy(x => x.Revision).ToListAsync(ct);
        if (changes.Count == 0) return 0;
        if (changes.Any(x => string.IsNullOrWhiteSpace(x.BaseNumber)))
            throw new DomainException("An approved Case package cannot raise Procedure work from an unnumbered Case change.");

        var alreadyRaised = await db.TestChangeReviews.AsNoTracking().AnyAsync(x =>
            x.OriginKind == TestChangeReviewOriginKind.CaseReview
            && x.OriginReferenceId == caseReview.Id
            && x.ArtifactKind == VerificationArtifactKind.Procedure
            && x.Discipline == caseReview.Discipline, ct);
        if (alreadyRaised || db.TestChangeReviews.Local.Any(x =>
                x.OriginKind == TestChangeReviewOriginKind.CaseReview
                && x.OriginReferenceId == caseReview.Id
                && x.ArtifactKind == VerificationArtifactKind.Procedure
                && x.Discipline == caseReview.Discipline))
            return 0;
        db.TestChangeReviews.Add(TestChangeReview.FromCaseReview(caseReview.ProjectId,
            caseReview.ReleaseId, caseReview.Id, procedureKey, caseReview.DisplayNumber, now));
        return 1;
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
        IReadOnlyList<MaterializedRequirementChange> changes, string actorId, DateTimeOffset now, CancellationToken ct)
    {
        var ladderPolicy = policyResolver is null
            ? fallbackPolicy
            : await policyResolver.ResolveAsync(projectId, ct);
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
        var changedRevisionIds = changes.Select(change => change.RevisionId).ToHashSet();
        var changedCoverage = db.TestCoverage.Local
            .Where(link => changedRevisionIds.Contains(link.RequirementRevisionId))
            .DistinctBy(link => link.ProcedureRevisionId)
            .ToList();
        await IncludeChangedCoverageInTestSetsAsync(projectId, releaseId, changes, changedCoverage,
            actorId, now, ct, ladderPolicy);
        var orphaned = await RaiseOrphanedProceduresAsync(projectId, releaseId, changes, carried, now, ct);
        return new MaterializationImpactResult(bound, carried.Count, confirmed, orphaned);
    }

    /// <summary>
    /// Every procedure already linked to wording this build modified is mandatory regression scope. Coverage
    /// is carried forward as suspect until engineering confirms it; the run obligation is immediate and is
    /// measured by the existing exact-revision result/evidence release gates.
    /// </summary>
    private async Task IncludeChangedCoverageInTestSetsAsync(Guid projectId, Guid releaseId,
        IReadOnlyList<MaterializedRequirementChange> changes, IReadOnlyList<TestRequirementCoverage> coverage,
        string actorId, DateTimeOffset now, CancellationToken ct, ILadderPolicy ladderPolicy)
    {
        if (coverage.Count == 0) return;
        var revisionIds = coverage.Select(x => x.ProcedureRevisionId).Distinct().ToList();
        var levels = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                            join procedure in db.TestProcedures.AsNoTracking().Where(x => x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case) on revision.ProcedureId equals procedure.Id
                            where revisionIds.Contains(revision.Id)
                            select new { revision.Id, procedure.Level }).ToListAsync(ct);
        var configuredProcedureLevels = ladderPolicy.OrderedLevels
            .Where(level => ladderPolicy.Definition(level).Verification is not null)
            .ToDictionary(ladderPolicy.ProcedureLevel, level => level);
        levels = levels.Where(row => configuredProcedureLevels.ContainsKey(row.Level)).ToList();
        var sets = await db.BuildTestSets.Include(x => x.Entries).Where(x => x.ReleaseId == releaseId).ToListAsync(ct);
        foreach (var discipline in ladderPolicy.OrderedLevels
                     .Where(level => ladderPolicy.Definition(level).Verification is not null)
                     .Select(ladderPolicy.Discipline))
        {
            if (sets.Any(x => x.Discipline == discipline)) continue;
            var pending = db.BuildTestSets.Local.FirstOrDefault(x => x.ReleaseId == releaseId && x.Discipline == discipline);
            var set = pending ?? new BuildTestSet(projectId, releaseId, discipline, now);
            if (pending is null) db.BuildTestSets.Add(set);
            sets.Add(set);
        }
        var changedByRevision = coverage.GroupBy(x => x.ProcedureRevisionId).ToDictionary(x => x.Key,
            x => changes.First(change => x.Any(link => link.RequirementRevisionId == change.RevisionId)).DisplayNumber);
        foreach (var row in levels)
        {
            var discipline = ladderPolicy.Discipline(configuredProcedureLevels[row.Level]);
            sets.Single(x => x.Discipline == discipline).Include(actorId, row.Id,
                TestSelectionReason.ChangedRequirement,
                $"Mandatory before release because {changedByRevision[row.Id]} changed.", now);
        }
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
                    $"{change.DisplayNumber} changed under this verification artifact, which was written against the previous wording.", now);
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
                x.RequirementRevisionId == change.RevisionId && x.ProcedureRevisionId == procedureRevisionId.Value)
                ?? await db.TestCoverage.SingleOrDefaultAsync(x =>
                    x.RequirementRevisionId == change.RevisionId
                    && x.ProcedureRevisionId == procedureRevisionId.Value, ct);
            if (existing is not null)
            {
                existing.ConfirmStillValid(item.ResolvedBy ?? "verification", now);
            }
            else
            {
                // Requirement materialisation is attributable for a newly-created requirement revision, but
                // it is not a second authoring route for an already approved Case/System Procedure. A missing
                // exact parent remains a resolved impact decision until the ordinary ModifyExisting successor
                // is reviewed and materialised; only a newly-added successor revision may receive this link.
                var procedureRevisionWasAdded = db.ChangeTracker.Entries<TestProcedureRevision>()
                    .Any(entry => entry.Entity.Id == procedureRevisionId.Value && entry.State == EntityState.Added);
                if (!procedureRevisionWasAdded) continue;
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
            .Join(db.TestProcedures.AsNoTracking().Where(p => p.ProjectId == projectId
                && (p.Level == TestProcedureLevel.System || p.ArtifactKind == VerificationArtifactKind.Case)),
                revision => revision.ProcedureId, procedure => procedure.Id,
                (revision, procedure) => new { procedure.Id, procedure.BaseNumber, procedure.Level })
            .Distinct()
            .ToListAsync(ct);
        if (orphanedProcedures.Count == 0) return 0;

        return await RaiseOrphanItemsAsync(projectId, releaseId, retired[0].ChangeRequestId, null,
            orphanedProcedures.Select(x => new OrphanedProcedure(x.Id, x.BaseNumber, x.Level)).ToList(), now, ct);
    }

    /// <summary>A procedure that no longer covers any requirement, and enough about it to route the work.</summary>
    public sealed record OrphanedProcedure(Guid ProcedureId, string DisplayNumber, TestProcedureLevel Level);

    /// <summary>
    /// Turns procedures that cover nothing into work somebody is assigned, whatever removed the requirement.
    ///
    /// Shared by the two things that can remove one. A retirement decides it, and the change request that
    /// decided is the whole story. A reopened baseline un-materializes it, and then the change request named
    /// here is the one whose work was taken back rather than the one that chose to take it -- so the baseline
    /// travels with the item and says which act it was.
    ///
    /// Deduplicated against every orphan item still open, not merely the ones raised in this pass: reopening a
    /// build twice, or retiring after a reopen, must not hand the same procedure to somebody twice.
    /// </summary>
    private async Task<int> RaiseOrphanItemsAsync(Guid projectId, Guid releaseId, Guid changeRequestId,
        Guid? causingBaselineId, IReadOnlyList<OrphanedProcedure> orphanedProcedures, DateTimeOffset now,
        CancellationToken ct)
    {
        var ladderPolicy = policyResolver is null
            ? fallbackPolicy
            : await policyResolver.ResolveAsync(projectId, ct);
        if (orphanedProcedures.Count == 0) return 0;
        var alreadyRaised = await db.VerificationImpactItems
            .Where(x => x.Trigger == VerificationImpactTrigger.ProcedureOrphaned
                && x.State != VerificationImpactState.Resolved && x.State != VerificationImpactState.Superseded)
            .Select(x => x.ProcedureId).ToListAsync(ct);
        var covered = alreadyRaised.Where(x => x is not null).Select(x => x!.Value).ToHashSet();
        // Items added earlier in this same unit of work are not in the query above, and one reopen can strand
        // two revisions of the same procedure.
        foreach (var pending in db.VerificationImpactItems.Local
                     .Where(x => x.Trigger == VerificationImpactTrigger.ProcedureOrphaned && x.ProcedureId is not null))
            covered.Add(pending.ProcedureId!.Value);
        var sourceNumber = await db.SystemChangeRequests.Where(x => x.Id == changeRequestId)
            .Select(x => x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision)
            .SingleAsync(ct);
        var reviews = await db.TestChangeReviews.Where(x => x.ChangeRequestId == changeRequestId)
            .ToDictionaryAsync(x => x.Discipline, ct);
        var configuredProcedureLevels = ladderPolicy.OrderedLevels
            .Where(level => ladderPolicy.Definition(level).Verification is not null)
            .ToDictionary(ladderPolicy.ProcedureLevel, level => level);

        var raised = 0;
        foreach (var procedure in orphanedProcedures)
        {
            if (!covered.Add(procedure.ProcedureId)) continue;
            if (!configuredProcedureLevels.TryGetValue(procedure.Level, out var procedureLevel)) continue;
            var discipline = ladderPolicy.Discipline(procedureLevel);
            if (!reviews.TryGetValue(discipline, out var review))
            {
                // An orphaned procedure is itself the finding: the change left a procedure without a
                // requirement, so test work is required and this is a controlled test change request from
                // the moment it exists.
                review = new TestChangeReview(projectId, releaseId, changeRequestId, discipline, sourceNumber, now);
                review.RecordTestChangeRequired("system.verification", now);
                review.AssignControlledNumber(await IdentifierAllocator.NextTestChangeRequestAsync(db, review.ArtifactKey, ct, ladderPolicy), now, ladderPolicy);
                db.TestChangeReviews.Add(review);
                reviews.Add(discipline, review);
            }
            db.VerificationImpactItems.Add(VerificationImpactItem.ForOrphanedProcedure(
                projectId, releaseId, changeRequestId, review.Id, procedure.ProcedureId, procedure.DisplayNumber,
                now, causingBaselineId));
            raised++;
        }
        return raised;
    }

    /// <summary>
    /// Raises work for the procedures a reopened baseline left covering nothing.
    ///
    /// The caller has already established which those are -- it is the thing taking the revisions back, so it
    /// is the only thing that knows what survived -- and this does the routing and the recording rather than
    /// the finding.
    /// </summary>
    public Task<int> RaiseProceduresOrphanedByReopenAsync(Guid projectId, Guid releaseId, Guid baselineId,
        Guid changeRequestId, IReadOnlyList<OrphanedProcedure> orphanedProcedures, DateTimeOffset now,
        CancellationToken ct)
        => RaiseOrphanItemsAsync(projectId, releaseId, changeRequestId, baselineId, orphanedProcedures, now, ct);

    /// <summary>Unresolved items are what hold a baseline back from approval.</summary>
    public Task<List<VerificationImpactItem>> OutstandingForReleaseAsync(Guid releaseId, CancellationToken ct)
        => db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.ReleaseId == releaseId && x.State != VerificationImpactState.Resolved
                && x.State != VerificationImpactState.Superseded)
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
                               join procedure in db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId
                                   && (x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case))
                                   on revision.ProcedureId equals procedure.Id
                               select new
                               {
                                   procedure.Id,
                                   RevisionId = revision.Id,
                                   revision.Revision,
                                   procedure.BaseNumber,
                                   Level = procedure.Level.ToString(),
                                   State = revision.State.ToString()
                               }).ToListAsync(ct);
        var selected = revisions.OrderByDescending(x => x.Revision).FirstOrDefault();
        if (selected is null) return null;
        var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
            [selected.RevisionId], ct);
        var title = titles[selected.RevisionId];
        return new ApprovedProcedureSelection(selected.Id, selected.RevisionId, selected.Revision,
            $"{selected.BaseNumber}.{selected.Revision:D2}", title.Title,
            title.IsExact, title.IsLegacy, title.Note,
            selected.Level, selected.State);
    }

    /// <summary>
    /// Records a retarget decision without rewriting an existing procedure revision.
    ///
    /// A retarget decision can confirm an existing #709 suspect link, but it cannot add a new exact parent to
    /// an already approved Case/System Procedure revision. That parent selection is immutable controlled
    /// content: a new link requires a TCR successor with its own signed selection and materialisation. The
    /// requirement-baseline materialiser has a separate attributable path for a newly-created requirement
    /// revision and is deliberately not routed through this endpoint helper.
    /// </summary>
    public async Task<bool> HasEffectiveRetargetTargetAsync(Guid projectId, Guid releaseId, Guid procedureId,
        Guid requirementRevisionId, CancellationToken ct)
    {
        var ladderPolicy = policyResolver is null
            ? fallbackPolicy
            : await policyResolver.ResolveAsync(projectId, ct);
        if (!await TestChangeReviewRequirementScope.IsExactRetargetTargetInBuildAsync(
                db, projectId, releaseId, procedureId, requirementRevisionId, ladderPolicy, ct))
            return false;
        var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, projectId, releaseId, ct);
        if (effectivity?.RevisionByProcedure.TryGetValue(procedureId, out var revisionId) != true)
            return false;
        return await db.TestCoverage.AsNoTracking().AnyAsync(x =>
            x.ProcedureRevisionId == revisionId && x.RequirementRevisionId == requirementRevisionId, ct);
    }

    /// <summary>Shared target-build gate used by the resolve endpoint before it chooses LinkExisting or ModifyExisting.</summary>
    public async Task<bool> IsExactRetargetTargetInBuildAsync(Guid projectId, Guid releaseId, Guid procedureId,
        Guid requirementRevisionId, CancellationToken ct)
    {
        var ladderPolicy = policyResolver is null
            ? fallbackPolicy
            : await policyResolver.ResolveAsync(projectId, ct);
        return await TestChangeReviewRequirementScope.IsExactRetargetTargetInBuildAsync(
            db, projectId, releaseId, procedureId, requirementRevisionId, ladderPolicy, ct);
    }

    public async Task<bool> ApplyRetargetedCoverageAsync(VerificationImpactItem item, DateTimeOffset now, CancellationToken ct)
    {
        if (item.Outcome != VerificationImpactOutcome.ProcedureRetargeted
            || item.ProcedureId is null
            || item.RetargetedRequirementRevisionId is null)
            return false;

        if (!await IsExactRetargetTargetInBuildAsync(item.ProjectId, item.ReleaseId,
                item.ProcedureId.Value, item.RetargetedRequirementRevisionId.Value, ct))
            return false;

        var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, item.ProjectId, item.ReleaseId, ct)
            ?? throw new DomainException("The retarget decision has no governed procedure baseline. Create a controlled successor revision in a candidate baseline.");
        var effectiveRevisionIds = effectivity.RevisionByProcedure.TryGetValue(item.ProcedureId.Value, out var effectiveRevisionId)
            ? new[] { effectiveRevisionId }
            : Array.Empty<Guid>();
        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => effectiveRevisionIds.Contains(x.Id))
            .Select(x => new { x.Id, x.State })
            .ToListAsync(ct);
        if (revisions.Count == 0) return false;

        var target = item.RetargetedRequirementRevisionId.Value;
        var already = await db.TestCoverage
            .Where(x => x.RequirementRevisionId == target && revisions.Select(r => r.Id).Contains(x.ProcedureRevisionId))
            .ToListAsync(ct);
        var linked = false;
        foreach (var revision in revisions)
        {
            var existing = already.SingleOrDefault(x => x.ProcedureRevisionId == revision.Id);
            if (existing is not null)
            {
                existing.ConfirmStillValid(item.ResolvedBy ?? "verification", now);
                linked = true;
                continue;
            }

            // The decision itself remains attributable to the Draft TCR. The new exact parent is deferred to
            // that package's controlled successor and materialisation; resolving the impact item must not become
            // a second authoring route for an already approved revision.
            return linked;
        }
        return linked;
    }

    /// <summary>
    /// Applies a coverage-confirmed decision only when an exact link already exists (including an existing
    /// suspect link). Before materialisation there is nothing to link, and a missing link after materialisation
    /// is deferred to an ordinary controlled successor rather than added through the impact endpoint.
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
            // A materialised requirement that has no existing link needs an ordinary controlled successor. The
            // impact decision remains recorded, while the missing link is intentionally deferred to that path.
            return false;
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

}
