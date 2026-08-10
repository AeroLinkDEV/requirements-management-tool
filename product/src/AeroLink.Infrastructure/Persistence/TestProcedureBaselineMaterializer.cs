using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;
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
public sealed class TestProcedureBaselineMaterializer(AeroLinkDbContext db)
{
    public async Task<TestProcedureMaterializationResult> MaterializeAsync(Guid baselineId, string actorId,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var baseline = await db.CandidateBaselines.Include(x => x.TestChangeSelections).Include(x => x.Events)
                           .SingleOrDefaultAsync(x => x.Id == baselineId, ct)
                       ?? throw new DomainException("Baseline not found.");
        if (baseline.State != CandidateBaselineState.Frozen)
            throw new DomainException("Freeze the baseline before materializing its test procedures.");
        if (baseline.RequirementsMaterializedAt is null)
            throw new DomainException("Materialize the requirement baseline before its test procedures — a procedure verifies a requirement that has to exist first.");
        if (baseline.TestProceduresMaterializedAt is not null)
            throw new DomainException("The test procedure baseline is already materialized and immutable.");

        var procedures = await db.TestProcedures.Where(x => x.ProjectId == baseline.ProjectId).ToListAsync(ct);
        var procedureByBase = procedures.ToDictionary(x => x.BaseNumber, StringComparer.OrdinalIgnoreCase);
        var current = await CarryForwardPredecessorAsync(baseline, ct);

        var tcrIds = baseline.TestChangeSelections.Select(x => x.TestChangeRequestId).ToList();
        var tcrs = await db.TestChangeReviews.AsNoTracking().Where(x => tcrIds.Contains(x.Id))
            .Include(x => x.ProcedureChanges).ToListAsync(ct);
        foreach (var tcr in tcrs.Where(x => x.State != TestChangeReviewState.Approved))
            throw new DomainException($"{tcr.DisplayNumber} is no longer approved and cannot be materialized.");
        // Validate the approved snapshots before adding any procedure, revision, coverage, or manifest row.
        // This is deliberately repeated here even though current API writes enforce the same scope: legacy or
        // malformed controlled data must fail closed at the boundary where it would become real coverage.
        await ValidateDrivingRequirementScopeAsync(baseline.Id, tcrs, procedureByBase, current, ct);

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
                    actorId, now, change.Level);
                db.TestProcedures.Add(procedure);
                procedureByBase.Add(procedure.BaseNumber, procedure);
                var revision = CreateRevision(procedure.Id, change, pair.tcr, baseline.Id, now, TestProcedureState.Approved);
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
        var settled = await SettleAwaitingDecisionsAsync(baseline.ProjectId, materialized, procedureByBase, actorId, now, ct);

        var procedureById = procedureByBase.Values.ToDictionary(x => x.Id);
        foreach (var item in current.OrderBy(x => procedureById[x.Key].BaseNumber))
            db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(baseline.Id, item.Key, item.Value.Id));
        var manifest = string.Join(";", current.OrderBy(x => procedureById[x.Key].BaseNumber)
            .Select(x => $"{procedureById[x.Key].BaseNumber}.{x.Value.Revision:D2}:{x.Value.Id}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        baseline.MarkTestProceduresMaterialized(actorId, hash, current.Count, now);
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
        // A predecessor from before build-scoped manifests existed has no members, and reading that as "it
        // carried no procedures" is only truthful for a project that genuinely has none. For one that already
        // holds a controlled procedure inventory it would publish a manifest omitting all of it, which claims
        // the build contains far less than it does. So the inventory is carried forward as the starting point.
        //
        // What this establishes is a migration snapshot, not evidence that the predecessor always held exactly
        // these revisions — nothing recorded that, and inventing it would be worse than the gap. It is taken
        // once: the successor's own manifest is immutable from the moment it is written.
        if (predecessor.TestProceduresMaterializedAt is null)
            return await LegacyInventoryAsync(baseline.ProjectId, ct);
        var items = await db.BaselineTestProcedures.AsNoTracking().Where(x => x.BaselineId == predecessor.Id).ToListAsync(ct);
        var revisionIds = items.Select(x => x.RevisionId).ToList();
        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => revisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var item in items) current[item.ProcedureId] = revisions[item.RevisionId];
        return current;
    }

    /// <summary>
    /// The project's controlled procedures as they stand, used as the starting point when no predecessor
    /// manifest exists.
    ///
    /// One revision per procedure: the highest-numbered approved one. A procedure whose latest revision is
    /// retired is left out, because it is not something the build carries. Deterministic, so two runs on the
    /// same data produce the same manifest hash.
    /// </summary>
    private async Task<Dictionary<Guid, TestProcedureRevision>> LegacyInventoryAsync(Guid projectId, CancellationToken ct)
    {
        var revisions = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                               join procedure in db.TestProcedures.AsNoTracking()
                                   on revision.ProcedureId equals procedure.Id
                               where procedure.ProjectId == projectId
                               select revision).ToListAsync(ct);
        return revisions
            .GroupBy(x => x.ProcedureId)
            .Select(group => group.OrderByDescending(x => x.Revision).First())
            .Where(x => x.State == TestProcedureState.Approved)
            .ToDictionary(x => x.ProcedureId);
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
            var driving = DrivingRequirements(entry.Change).Distinct().ToHashSet();
            var removed = RemovedRequirements(entry.Change).Distinct().ToHashSet();
            var prior = entry.PriorRevisionId is null
                ? []
                : await db.TestCoverage.AsNoTracking()
                    .Where(x => x.ProcedureRevisionId == entry.PriorRevisionId.Value
                        && carriedRequirementIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
            foreach (var predecessor in prior.Where(x => !removed.Contains(x.RequirementRevisionId)))
            {
                db.TestCoverage.Add(driving.Contains(predecessor.RequirementRevisionId)
                    ? new TestRequirementCoverage(entry.RevisionId, predecessor.RequirementRevisionId)
                    : TestRequirementCoverage.RetainedByProcedureRevision(entry.RevisionId, predecessor));
                added++;
            }
            var priorIds = prior.Select(x => x.RequirementRevisionId).ToHashSet();
            foreach (var requirementRevisionId in driving.Where(x => !priorIds.Contains(x)))
            {
                db.TestCoverage.Add(new TestRequirementCoverage(entry.RevisionId, requirementRevisionId));
                added++;
            }
        }
        return added;

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
                var driving = DrivingRequirements(change).Distinct().ToHashSet();
                var removed = RemovedRequirements(change).Distinct().ToHashSet();
                if (driving.Overlaps(removed))
                    throw new DomainException($"{change.DisplayNumber} both adds and removes the same requirement coverage.");
                var outside = driving.Concat(removed)
                    .FirstOrDefault(x => !governedIds.Contains(x));
                if (outside != Guid.Empty)
                    throw new DomainException(
                        $"{change.DisplayNumber} names requirement revision {outside}, which is outside {tcr.DisplayNumber}'s governed package/build scope.");
                if (change.Kind != TestProcedureChangeKind.Modify) continue;
                if (!procedureByBase.TryGetValue(change.BaseNumber, out var procedure)
                    || !current.TryGetValue(procedure.Id, out var priorRevision))
                    throw new DomainException($"{change.DisplayNumber} has no carried predecessor coverage to modify.");
                var priorIds = (await db.TestCoverage.AsNoTracking()
                        .Where(x => x.ProcedureRevisionId == priorRevision.Id
                            && db.BaselineRequirements.Any(b => b.BaselineId == baselineId
                                && b.RevisionId == x.RequirementRevisionId))
                        .Select(x => x.RequirementRevisionId).ToListAsync(ct)).ToHashSet();
                var absent = removed.FirstOrDefault(x => !priorIds.Contains(x));
                if (absent != Guid.Empty)
                    throw new DomainException(
                        $"{change.DisplayNumber} cannot remove requirement revision {absent} because its predecessor does not cover it.");
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
            .Where(x => x.Change.Kind != TestProcedureChangeKind.Retire)
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
                $"The requested procedure {match.Change.DisplayNumber} was approved in {match.Tcr.DisplayNumber} and now covers this requirement.",
                actorId, now));
            settled++;
        }
        return settled;
    }

    private static TestProcedureRevision CreateRevision(Guid procedureId, TestProcedureChange change,
        TestChangeReview tcr, Guid baselineId, DateTimeOffset now, TestProcedureState state) =>
        new(procedureId, change.Revision, change.Objective, change.Preconditions, change.Steps,
            change.ExpectedResult, state, tcr.SubmittedBy ?? tcr.DecidedBy ?? "aerolink.lifecycle", now,
            null, tcr.Id, baselineId);

    private static IReadOnlyList<Guid> DrivingRequirements(TestProcedureChange change)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(change.DrivingRequirementRevisionIdsJson) ?? []; }
        catch (JsonException) { return []; }
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
