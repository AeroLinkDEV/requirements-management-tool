using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Traceability;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record TestProcedureMaterializationResult(string ProceduresHash, int ActiveProcedureCount,
    int CreatedRevisionCount, int CoverageLinkCount, int SettledDecisionCount);

/// <summary>
/// Turns the approved procedure decisions selected into a baseline into controlled procedure revisions.
///
/// The test-procedure twin of <see cref="RequirementBaselineMaterializer"/>, and deliberately its mirror: the
/// predecessor's procedures carry forward, each proposal introduces, modifies or retires exactly one procedure,
/// and the resulting set is fixed with a manifest hash. Nothing a test change request proposes is a controlled
/// procedure revision until it passes through here — the same line the requirements side draws between a
/// proposal and a revision.
///
/// Runs after the requirement baseline rather than with it. A procedure verifies a requirement, so the
/// requirement revisions have to exist before a procedure revision can be bound to one.
/// </summary>
public sealed class TestProcedureBaselineMaterializer(AeroLinkDbContext db,
    ILadderPolicy? policy = null, IProjectLadderPolicyResolver? policyResolver = null)
{
    private sealed record ProcedureSourceSnapshot(
        Guid ChangeRequestId, string ChangeRequestNumber, bool Originating);
    public async Task<TestProcedureMaterializationResult> MaterializeAsync(Guid baselineId, string actorId,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var baseline = await db.CandidateBaselines.Include(x => x.TestChangeSelections).Include(x => x.Events)
                           .SingleOrDefaultAsync(x => x.Id == baselineId, ct)
                       ?? throw new DomainException("Baseline not found.");
        var ladderPolicy = policyResolver is null
            ? (policy ?? LegacyLadderPolicy.Instance)
            : await policyResolver.ResolveAsync(baseline.ProjectId, ct);
        using var savePolicyScope = db.UseSaveBoundaryPolicy(ladderPolicy);
        if (baseline.State != CandidateBaselineState.Frozen)
            throw new DomainException("Freeze the baseline before materializing its verification artifacts.");
        if (baseline.RequirementsMaterializedAt is null)
            throw new DomainException("Materialize the requirement baseline before its verification artifacts — an artifact verifies a requirement that has to exist first.");
        if (baseline.TestProceduresMaterializedAt is not null)
            throw new DomainException("The verification artifact baseline is already materialized and immutable.");

        // #726: baseline membership is the ENABLED artifact set for each level. With the software
        // Procedure tier enabled, Procedure revisions from Procedure TCRs materialize alongside the Case
        // revisions they satisfy; Case-only software and System keep their current membership.
        var enabledBindings = EffectiveExecutableArtifact.EnabledBindings(ladderPolicy);
        var procedures = await db.TestProcedures.Where(x => x.ProjectId == baseline.ProjectId
            && enabledBindings.Any(binding => binding.Level == x.Level && binding.Kind == x.ArtifactKind))
            .ToListAsync(ct);
        var procedureByBase = procedures.ToDictionary(x => x.BaseNumber, StringComparer.OrdinalIgnoreCase);
        var current = await CarryForwardPredecessorAsync(baseline, ct);

        var tcrIds = baseline.TestChangeSelections.Select(x => x.TestChangeRequestId).ToList();
        var tcrs = await db.TestChangeReviews.AsNoTracking().Where(x => tcrIds.Contains(x.Id))
            .Include(x => x.ProcedureChanges).Include(x => x.AdditionalSources).ToListAsync(ct);
        foreach (var tcr in tcrs.Where(x => x.State != TestChangeReviewState.Approved))
            throw new DomainException($"{tcr.DisplayNumber} is no longer approved and cannot be materialized.");
        // Validate the approved snapshots before adding any procedure, revision, coverage, or manifest row.
        // This is deliberately repeated here even though current API writes enforce the same scope: legacy or
        // malformed controlled data must fail closed at the boundary where it would become real coverage.
        await ValidateDrivingRequirementScopeAsync(baseline.Id, tcrs, procedureByBase, current, ct);
        foreach (var tcr in tcrs)
            await TestChangeReviewRequirementScope.ValidateRetargetPlansForSubmissionAsync(db, tcr, ct, ladderPolicy);

        var created = 0;
        // What each proposal became, so the requirement links it proposed can bind to a revision that exists.
        var materialized = new List<(TestChangeReview Tcr, TestProcedureChange Change, Guid RevisionId,
            Guid? PriorRevisionId)>();
        foreach (var pair in tcrs.SelectMany(tcr => tcr.ProcedureChanges.Select(change => new { tcr, change }))
                     .OrderBy(x => x.tcr.DisplayNumber).ThenBy(x => x.change.BaseNumber).ThenBy(x => x.change.Revision))
        {
            var change = pair.change;
            if (change.Kind == TestProcedureChangeKind.Introduce)
            {
                if (procedureByBase.ContainsKey(change.BaseNumber))
                    throw new DomainException($"{change.DisplayNumber} cannot be introduced because its stable identity already exists.");
                var procedure = new TestProcedure(baseline.ProjectId, change.BaseNumber, change.Title,
                    actorId, now, change.Level, ladderPolicy);
                db.TestProcedures.Add(procedure);
                procedureByBase.Add(procedure.BaseNumber, procedure);
                var revision = CreateRevision(procedure.Id, change, pair.tcr, baseline.Id, now,
                    TestProcedureState.Approved);
                db.TestProcedureRevisions.Add(revision);
                current[procedure.Id] = revision;
                created++;
                materialized.Add((pair.tcr, change, revision.Id, null));
                continue;
            }

            if (!procedureByBase.TryGetValue(change.BaseNumber, out var existing) || !current.TryGetValue(existing.Id, out var prior))
                throw new DomainException($"{change.Kind} requires {change.BaseNumber} to be active in the predecessor or current baseline.");
            if (change.Revision <= prior.Revision)
                throw new DomainException($"{change.DisplayNumber} must have a revision greater than {prior.Revision:D2}.");
            var state = change.Kind == TestProcedureChangeKind.Retire ? TestProcedureState.Retired : TestProcedureState.Approved;
            var next = CreateRevision(existing.Id, change, pair.tcr, baseline.Id, now, state);
            db.TestProcedureRevisions.Add(next);
            created++;
            materialized.Add((pair.tcr, change, next.Id, prior.Id));
            if (state == TestProcedureState.Retired)
            {
                current.Remove(existing.Id);
                continue;
            }
            existing.UpdateDraft(change.Title, existing.OwnerId, now);
            current[existing.Id] = next;
        }

        var coverageLinks = await LinkDrivingRequirementsAsync(baseline.Id, materialized, ct);
        await CarryCaseProcedureLinksAsync(baseline.ProjectId, baseline.DisplayNumber, materialized,
            actorId, now, ladderPolicy, ct);
        var settled = await SettleAwaitingDecisionsAsync(baseline.ProjectId, materialized, procedureByBase, actorId, now, ct);

        var procedureById = procedureByBase.Values.ToDictionary(x => x.Id);
        foreach (var item in current.OrderBy(x => procedureById[x.Key].BaseNumber))
            db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(baseline.Id, item.Key, item.Value.Id));
        var manifestEntries = current.Select(x => new TestProcedureManifestEntry(
            x.Key, x.Value.Id, procedureById[x.Key].BaseNumber, x.Value.Revision));
        var hash = TestProcedureManifest.Hash(manifestEntries);
        baseline.MarkTestProceduresMaterialized(actorId, hash, current.Count, now);
        await db.SaveChangesAsync(ct);

        // A procedure that exists but is written into no document is one the Explorer cannot show under any
        // document, and a section count that is quietly wrong. A requirement is authored into SYSRD, HLRD or
        // LLRD as part of becoming one; a procedure becomes one here, so it is filed here.
        //
        // After the save above rather than before it, because the placement reads the procedures back from
        // the database — and still inside the transaction, so a procedure and its place in a document are
        // committed together or not at all.
        await new TestProcedureDocumentBootstrap(db, ladderPolicy).EnsureForProjectAsync(baseline.ProjectId, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new TestProcedureMaterializationResult(hash, current.Count, created, coverageLinks, settled);
    }

    /// <summary>
    /// The procedures the predecessor baseline carried, which this one keeps unless a proposal changes them.
    ///
    /// A build does not re-approve every procedure it inherits, exactly as it does not re-approve every
    /// requirement. Without this the first materialization of a successor would drop everything nobody happened
    /// to touch.
    /// </summary>
    private async Task<Dictionary<Guid, TestProcedureRevision>> CarryForwardPredecessorAsync(
        CandidateBaseline baseline, CancellationToken ct)
    {
        var current = new Dictionary<Guid, TestProcedureRevision>();
        if (baseline.PredecessorBaselineId is null) return current;
        var predecessor = await db.CandidateBaselines.AsNoTracking()
                              .SingleOrDefaultAsync(x => x.Id == baseline.PredecessorBaselineId, ct)
                          ?? throw new DomainException("The predecessor baseline does not exist.");
        if (predecessor.ProjectId != baseline.ProjectId)
            throw new DomainException("The predecessor must be a baseline from the same project.");
        // A missing predecessor manifest is not an empty manifest. It is an unresolved migration boundary:
        // only the explicit, attributable legacy-bootstrap action may turn the old controlled inventory into
        // exact predecessor membership. Silently consulting today's inventory here would claim historical
        // precision while materializing an unrelated successor.
        if (predecessor.TestProceduresMaterializedAt is null)
            throw new DomainException(
                $"Predecessor {predecessor.DisplayNumber} has no exact verification artifact manifest. A Configuration Manager must establish its legacy bootstrap snapshot before this successor can materialize verification artifacts.");
        var items = await db.BaselineTestProcedures.AsNoTracking().Where(x => x.BaselineId == predecessor.Id).ToListAsync(ct);
        var revisionIds = items.Select(x => x.RevisionId).ToList();
        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => revisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var item in items) current[item.ProcedureId] = revisions[item.RevisionId];
        return current;
    }

    /// <summary>
    /// Turns each proposal's driving requirement revisions into real coverage.
    ///
    /// The proposal named requirement revisions; only now does a procedure revision exist for them to bind to.
    /// This is the same point at which a requirement change's proposed upstream allocation becomes a trace link.
    /// </summary>
    private async Task<int> LinkDrivingRequirementsAsync(Guid baselineId,
        IReadOnlyList<(TestChangeReview Tcr, TestProcedureChange Change, Guid RevisionId,
            Guid? PriorRevisionId)> materialized,
        CancellationToken ct)
    {
        var carriedRequirementIds = (await db.BaselineRequirements.AsNoTracking()
            .Where(x => x.BaselineId == baselineId).Select(x => x.RevisionId).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var entry in materialized.Where(x => x.Change.Kind != TestProcedureChangeKind.Retire))
        {
            // Derived Case/Procedure revisions are deliberately standalone. They
            // remain in the baseline, but do not create requirement coverage that
            // could satisfy an upstream obligation.
            var driving = DrivingRequirements(entry.Change).Distinct().ToHashSet();
            var removed = RemovedRequirements(entry.Change).Distinct().ToHashSet();
            var prior = entry.PriorRevisionId is null
                ? []
                : await db.TestCoverage.AsNoTracking()
                    .Where(x => x.ProcedureRevisionId == entry.PriorRevisionId.Value
                        && carriedRequirementIds.Contains(x.RequirementRevisionId)
                        // #709 suspect carry-forward is lifecycle evidence, not an approved parent to copy
                        // into the new revision. A fresh explicit selection below creates a non-suspect link.
                        && !x.IsSuspect
                        && entry.Change.ParentKind != VerificationProcedureParentKind.Derived).ToListAsync(ct);
            if (entry.Change.ParentKind == VerificationProcedureParentKind.Derived)
                continue;

            // ParentRevisionIdsJson is the immutable full selection for a new
            // package. Older authoring callers only supplied the driving delta,
            // however, and the aggregate retained that delta in the parent
            // field for compatibility. For a modification, resolve that
            // legacy shape against the predecessor before creating links. A
            // retained link is then copied through the #709 lifecycle rather
            // than silently discarded or treated as a newly authored link.
            var declaredParents = ParentRequirements(entry.Change).Distinct().ToHashSet();
            var finalParents = entry.Change.Kind == TestProcedureChangeKind.Modify
                && entry.Tcr.CaseContractVersion < TestChangeReview.CurrentCaseContractVersion
                && declaredParents.SetEquals(driving)
                ? prior.Select(x => x.RequirementRevisionId).Except(removed).Concat(driving).ToHashSet()
                : declaredParents;
            var expectedFinalParents = entry.Change.Kind == TestProcedureChangeKind.Modify
                ? prior.Select(x => x.RequirementRevisionId).Except(removed).Concat(driving).ToHashSet()
                : driving;
            if (!finalParents.SetEquals(expectedFinalParents))
                throw new DomainException(
                    $"{entry.Change.DisplayNumber} does not carry the exact final parent selection for its coverage delta.");
            if (!driving.IsSubsetOf(finalParents))
                throw new DomainException($"{entry.Change.DisplayNumber} driving requirement deltas are not contained in its exact final parent selection.");
            if (finalParents.Overlaps(removed))
                throw new DomainException($"{entry.Change.DisplayNumber} retains a requirement that it also removes.");

            // DrivingRequirementRevisionIdsJson is the delta. The immutable
            // ParentRevisionIdsJson is the complete final selection. Retained
            // predecessors must use #709's existing lifecycle so suspect and
            // confirmation evidence is carried without changing released history.
            var produced = new HashSet<Guid>();
            foreach (var predecessor in prior.Where(x => finalParents.Contains(x.RequirementRevisionId)))
            {
                db.TestCoverage.Add(TestRequirementCoverage.RetainedByProcedureRevision(entry.RevisionId, predecessor));
                produced.Add(predecessor.RequirementRevisionId);
                added++;
            }
            var priorIds = prior.Select(x => x.RequirementRevisionId).ToHashSet();
            foreach (var requirementRevisionId in finalParents.Where(x => !priorIds.Contains(x)))
            {
                db.TestCoverage.Add(new TestRequirementCoverage(entry.RevisionId, requirementRevisionId));
                produced.Add(requirementRevisionId);
                added++;
            }
            if (!produced.SetEquals(finalParents))
                throw new DomainException($"{entry.Change.DisplayNumber} materialized coverage does not equal its exact final parent selection.");
        }
        return added;

    }

    /// <summary>
    /// Carries each direct exact Case-to-Procedure relationship onto a newly materialized Case revision and
    /// attaches #709's shared suspect lifecycle. The predecessor link is immutable history; the new link is
    /// the only relationship whose current validity is unsettled.
    /// </summary>
    private async Task<int> CarryCaseProcedureLinksAsync(Guid projectId, string baselineNumber,
        IReadOnlyList<(TestChangeReview Tcr, TestProcedureChange Change, Guid RevisionId,
            Guid? PriorRevisionId)> materialized, string actorId, DateTimeOffset now,
        ILadderPolicy ladderPolicy, CancellationToken ct)
    {
        var changedCases = materialized.Where(x =>
                x.Tcr.ArtifactKind == VerificationArtifactKind.Case
                && x.Change.Kind == TestProcedureChangeKind.Modify
                && x.PriorRevisionId is not null)
            .Where(x => ladderPolicy.VerificationProfile(
                    ladderPolicy.RequirementLevelFor(x.Tcr.Discipline))
                .Enables(VerificationArtifactKind.Procedure))
            .ToList();
        if (changedCases.Count == 0) return 0;

        var priorIds = changedCases.Select(x => x.PriorRevisionId!.Value).Distinct().ToList();
        var predecessors = await db.TestCaseProcedureLinks.AsNoTracking()
            .Where(x => priorIds.Contains(x.CaseRevisionId)).ToListAsync(ct);
        var existing = (await db.TestCaseProcedureLinks.AsNoTracking()
                .Where(x => changedCases.Select(change => change.RevisionId).Contains(x.CaseRevisionId))
                .Select(x => new { x.CaseRevisionId, x.ProcedureRevisionId }).ToListAsync(ct))
            .Select(x => (x.CaseRevisionId, x.ProcedureRevisionId)).ToHashSet();
        foreach (var pending in db.TestCaseProcedureLinks.Local)
            existing.Add((pending.CaseRevisionId, pending.ProcedureRevisionId));

        var carried = 0;
        foreach (var change in changedCases)
        {
            foreach (var predecessor in predecessors.Where(x => x.CaseRevisionId == change.PriorRevisionId))
            {
                if (!existing.Add((change.RevisionId, predecessor.ProcedureRevisionId))) continue;
                var link = new TestCaseProcedureLink(change.RevisionId, predecessor.ProcedureRevisionId);
                var lifecycle = ExactLinkSuspectLifecycle.Raise(projectId, ExactLinkKind.CaseProcedure,
                    link.Id, ExactLinkLifecycleCauseKind.InternalVerificationRevision, null, null,
                    actorId,
                    $"The exact Case revision changed from {predecessor.CaseRevisionId} to {change.RevisionId} in baseline {baselineNumber}; its direct Procedure relationship requires reassessment.",
                    now, change.RevisionId);
                link.AttachExactLinkLifecycle(lifecycle.Id);
                db.TestCaseProcedureLinks.Add(link);
                db.ExactLinkSuspectLifecycles.Add(lifecycle);
                db.ExactLinkSuspectEvents.AddRange(lifecycle.Events);
                carried++;
            }
        }
        return carried;
    }

    private async Task ValidateDrivingRequirementScopeAsync(Guid baselineId,
        IReadOnlyCollection<TestChangeReview> tcrs,
        IReadOnlyDictionary<string, TestProcedure> procedureByBase,
        IReadOnlyDictionary<Guid, TestProcedureRevision> current, CancellationToken ct)
    {
        foreach (var tcr in tcrs)
        {
            var governed = await TestChangeReviewRequirementScope.ForReviewAsync(db, tcr, baselineId, ct);
            var governedIds = governed.Select(x => x.RevisionId).ToHashSet();
            foreach (var change in tcr.ProcedureChanges.Where(x => x.Kind != TestProcedureChangeKind.Retire))
            {
                var driving = ParentRequirements(change).Distinct().ToHashSet();
                var removed = RemovedRequirements(change).Distinct().ToHashSet();
                var parentIds = ParseParentIds(change.ParentRevisionIdsJson, change.DisplayNumber);
                ExactParentSelectionPolicy.Validate(
                    VerificationProcedureParentPolicy.Classification(change.ParentKind), parentIds,
                    change.DerivedRationale, tcr.Discipline == TestChangeReviewDiscipline.System
                        ? "System Procedure"
                        : "software Case");
                if (change.ParentKind == VerificationProcedureParentKind.Derived)
                    continue;
                if (driving.Overlaps(removed))
                    throw new DomainException($"{change.DisplayNumber} both adds and removes the same requirement coverage.");
                HashSet<Guid> priorIds = [];
                if (change.Kind == TestProcedureChangeKind.Modify)
                {
                    if (!procedureByBase.TryGetValue(change.BaseNumber, out var procedure)
                        || !current.TryGetValue(procedure.Id, out var priorRevision))
                        throw new DomainException($"{change.DisplayNumber} has no carried predecessor coverage to modify.");
                    priorIds = (await db.TestCoverage.AsNoTracking()
                            .Where(x => x.ProcedureRevisionId == priorRevision.Id
                                && !x.IsSuspect
                                && db.BaselineRequirements.Any(b => b.BaselineId == baselineId
                                    && b.RevisionId == x.RequirementRevisionId))
                            .Select(x => x.RequirementRevisionId).ToListAsync(ct)).ToHashSet();
                    var absent = removed.FirstOrDefault(x => !priorIds.Contains(x));
                    if (absent != Guid.Empty)
                        throw new DomainException(
                            $"{change.DisplayNumber} cannot remove requirement revision {absent} because its predecessor does not cover it.");
                }
                // Retained exact parents are governed by the predecessor
                // procedure's current build coverage even when the new TCR's
                // impact package only names the changed requirement. They are
                // not a fresh out-of-scope allocation.
                var outside = parentIds.Concat(removed)
                    .FirstOrDefault(x => !governedIds.Contains(x) && !priorIds.Contains(x));
                if (outside != Guid.Empty)
                    throw new DomainException(
                        $"{change.DisplayNumber} names requirement revision {outside}, which is outside {tcr.DisplayNumber}'s governed package/build scope.");
                if (change.Kind != TestProcedureChangeKind.Modify) continue;
                if ((removed.Count != 0 || driving.Any(x => !priorIds.Contains(x)))
                    && string.IsNullOrWhiteSpace(change.CoverageChangeRationale))
                    throw new DomainException(
                        $"{change.DisplayNumber} changes requirement coverage without an approved rationale.");
            }
        }
    }

    /// <summary>
    /// The revision as approved, credited to the engineer who authored the package rather than to whoever
    /// happened to run the materialization.
    /// </summary>
    /// <summary>
    /// Settles the decisions that asked for these procedures, now that the procedures exist.
    ///
    /// The direct-authoring endpoint already does this when an engineer approves a procedure by hand. A
    /// procedure produced by a test change request never passes through that endpoint — it comes into existence
    /// here, already approved — so without this an item that said "a new procedure is required" would sit
    /// unsettled forever while the procedure it asked for sat in the build. The engineer would have to answer
    /// the same question a second time, and the coverage gate would keep holding against work already done.
    ///
    /// Deliberately as narrow as its counterpart: only items awaiting a new procedure, and only for the exact
    /// requirement revisions the new procedure actually covers.
    /// </summary>
    private async Task<int> SettleAwaitingDecisionsAsync(Guid projectId,
        IReadOnlyList<(TestChangeReview Tcr, TestProcedureChange Change, Guid RevisionId,
            Guid? PriorRevisionId)> materialized,
        IReadOnlyDictionary<string, TestProcedure> procedureByBase, string actorId, DateTimeOffset now,
        CancellationToken ct)
    {
        var byRequirement = materialized
            .Where(x => x.Change.Kind != TestProcedureChangeKind.Retire
                && x.Change.ParentKind != VerificationProcedureParentKind.Derived)
            // Settlement is attributable to the driving/addition delta, not every retained parent in a
            // successor's immutable final selection. A retained parent may be present merely because the
            // successor carries an existing link forward; it must not settle an unrelated NewProcedureRequired
            // decision that this package never addressed.
            .SelectMany(x => DrivingRequirements(x.Change).Select(requirementRevisionId => new
            {
                requirementRevisionId,
                x.Tcr,
                x.Change,
                x.RevisionId
            }))
            .ToList();
        if (byRequirement.Count == 0) return 0;

        var requirementRevisionIds = byRequirement.Select(x => x.requirementRevisionId).Distinct().ToList();
        var awaiting = await db.VerificationImpactItems
            .Where(x => x.ProjectId == projectId
                && x.State == VerificationImpactState.Resolved
                && x.Outcome == VerificationImpactOutcome.NewProcedureRequired
                && x.RequirementRevisionId != null
                && requirementRevisionIds.Contains(x.RequirementRevisionId.Value))
            .ToListAsync(ct);

        var settled = 0;
        foreach (var item in awaiting)
        {
            var match = byRequirement.FirstOrDefault(x => x.requirementRevisionId == item.RequirementRevisionId!.Value);
            if (match is null) continue;
            var procedure = procedureByBase[match.Change.BaseNumber];
            if (!item.SettleWithApprovedProcedure(procedure.Id, match.RevisionId, now)) continue;
            db.VerificationImpactDecisionHistory.Add(new VerificationImpactDecisionHistory(
                item.Id, VerificationImpactHistoryAction.Resolved,
                VerificationImpactOutcome.ProcedureCoverageConfirmed, procedure.Id, match.RevisionId,
                $"The requested {(match.Change.Level == TestProcedureLevel.System ? "procedure" : "case")} {match.Change.DisplayNumber} was approved in {match.Tcr.DisplayNumber} and now covers this requirement.",
                actorId, now));
            settled++;
        }
        return settled;
    }


private static TestProcedureRevision CreateRevision(Guid procedureId, TestProcedureChange change,
    TestChangeReview tcr, Guid baselineId, DateTimeOffset now, TestProcedureState state) =>
    new(procedureId, change.Revision, change.Objective, change.Preconditions, change.Steps,
        change.ExpectedResult, state, tcr.SubmittedBy ?? tcr.DecidedBy ?? "aerolink.lifecycle", now,
        null, tcr.Id, baselineId, SourceSnapshotJson(tcr),
        parentKind: change.ParentKind, derivedRationale: change.DerivedRationale);

private static string SourceSnapshotJson(TestChangeReview tcr)
{
    // Only a package raised from a change request contributes an originating source. One raised from a
    // Problem Report records no change-request origin here rather than inventing one — this snapshot is a
    // record of the change requests a materialized procedure answers for, and there is none.
    var sources = (tcr.ChangeRequestId is { } originating
            ? new[]
            {
                new ProcedureSourceSnapshot(originating, tcr.SourceChangeRequestNumber, true),
            }
            : [])
        .Concat(tcr.AdditionalSources.Select(x => new ProcedureSourceSnapshot(
            x.ChangeRequestId, x.ChangeRequestNumber, false)))
        .DistinctBy(x => x.ChangeRequestId)
        .OrderBy(x => x.ChangeRequestId)
        .ToList();
    return JsonSerializer.Serialize(sources);
}

    private static IReadOnlyList<Guid> DrivingRequirements(TestProcedureChange change)
    {
        try
        {
            return ExactParentSelectionPolicy.NormalizeIds(
                JsonSerializer.Deserialize<List<Guid>>(string.IsNullOrWhiteSpace(change.DrivingRequirementRevisionIdsJson)
                    ? "[]" : change.DrivingRequirementRevisionIdsJson) ?? [], change.DisplayNumber);
        }
        catch (JsonException)
        {
            throw new DomainException($"{change.DisplayNumber} carries malformed driving requirement revisions.");
        }
    }

    private static IReadOnlyList<Guid> ParentRequirements(TestProcedureChange change)
    {
        if (change.ParentKind == VerificationProcedureParentKind.Derived)
            return [];
        var json = string.IsNullOrWhiteSpace(change.ParentRevisionIdsJson)
            || change.ParentRevisionIdsJson.Trim() == "[]"
            ? change.DrivingRequirementRevisionIdsJson
            : change.ParentRevisionIdsJson;
        return ParseParentIds(json, change.DisplayNumber);
    }

    private static IReadOnlyList<Guid> ParseParentIds(string json, string displayNumber)
    {
        try
        {
            return ExactParentSelectionPolicy.NormalizeIds(
                JsonSerializer.Deserialize<List<Guid>>(string.IsNullOrWhiteSpace(json) ? "[]" : json)
                    ?? [], displayNumber);
        }
        catch (JsonException)
        {
            throw new DomainException($"{displayNumber} carries malformed exact parent revisions.");
        }
    }

    private static IReadOnlyList<Guid> RemovedRequirements(TestProcedureChange change)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(change.RemovedRequirementRevisionIdsJson) ?? []; }
        catch (JsonException)
        {
            throw new DomainException($"{change.DisplayNumber} carries malformed removed requirement revisions.");
        }
    }
}
