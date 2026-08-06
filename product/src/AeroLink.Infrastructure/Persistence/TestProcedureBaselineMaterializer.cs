using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record TestProcedureMaterializationResult(string ProceduresHash, int ActiveProcedureCount,
    int CreatedRevisionCount, int CoverageLinkCount);

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
        if (baseline.State == CandidateBaselineState.Draft)
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

        var created = 0;
        // What each proposal became, so the requirement links it proposed can bind to a revision that exists.
        var materialized = new List<(TestProcedureChange Change, Guid RevisionId)>();
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
                materialized.Add((change, revision.Id));
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
            materialized.Add((change, next.Id));
            if (state == TestProcedureState.Retired)
            {
                current.Remove(existing.Id);
                continue;
            }
            existing.UpdateDraft(change.Title, existing.OwnerId, now);
            current[existing.Id] = next;
        }

        var coverageLinks = await LinkDrivingRequirementsAsync(materialized, ct);

        var procedureById = procedureByBase.Values.ToDictionary(x => x.Id);
        foreach (var item in current.OrderBy(x => procedureById[x.Key].BaseNumber))
            db.BaselineTestProcedures.Add(new BaselineTestProcedureSelection(baseline.Id, item.Key, item.Value.Id));
        var manifest = string.Join(";", current.OrderBy(x => procedureById[x.Key].BaseNumber)
            .Select(x => $"{procedureById[x.Key].BaseNumber}.{x.Value.Revision:D2}:{x.Value.Id}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        baseline.MarkTestProceduresMaterialized(actorId, hash, current.Count, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new TestProcedureMaterializationResult(hash, current.Count, created, coverageLinks);
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
        // A predecessor that never materialized its procedures is not an error. It is a build from before this
        // existed, and it genuinely carries none — starting empty is the truthful reading, not a failure.
        if (predecessor.TestProceduresMaterializedAt is null) return current;
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
    private async Task<int> LinkDrivingRequirementsAsync(
        IReadOnlyList<(TestProcedureChange Change, Guid RevisionId)> materialized, CancellationToken ct)
    {
        var wanted = materialized
            .Where(x => x.Change.Kind != TestProcedureChangeKind.Retire)
            .SelectMany(x => DrivingRequirements(x.Change).Select(requirementRevisionId => (x.RevisionId, requirementRevisionId)))
            .Distinct().ToList();
        if (wanted.Count == 0) return 0;

        // A named revision that no longer exists is a stale identifier from a draft, not an instruction to
        // create a link to nothing — the same treatment a stale section identifier gets on the requirement side.
        var requirementRevisionIds = wanted.Select(x => x.requirementRevisionId).Distinct().ToList();
        var real = (await db.RequirementRevisions.AsNoTracking()
            .Where(x => requirementRevisionIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct)).ToHashSet();

        var existing = (await db.TestCoverage.AsNoTracking()
                .Where(x => requirementRevisionIds.Contains(x.RequirementRevisionId))
                .Select(x => new { x.ProcedureRevisionId, x.RequirementRevisionId }).ToListAsync(ct))
            .Select(x => (x.ProcedureRevisionId, x.RequirementRevisionId)).ToHashSet();

        var added = 0;
        foreach (var (procedureRevisionId, requirementRevisionId) in wanted)
        {
            if (!real.Contains(requirementRevisionId)) continue;
            if (!existing.Add((procedureRevisionId, requirementRevisionId))) continue;
            db.TestCoverage.Add(new TestRequirementCoverage(procedureRevisionId, requirementRevisionId));
            added++;
        }
        return added;
    }

    /// <summary>
    /// The revision as approved, credited to the engineer who authored the package rather than to whoever
    /// happened to run the materialization.
    /// </summary>
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
}
