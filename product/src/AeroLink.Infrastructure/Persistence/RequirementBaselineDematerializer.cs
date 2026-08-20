using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>A change request left pointing at a revision the reopen took back.</summary>
public sealed record StrandedChangeRequest(Guid ChangeRequestId, string DisplayNumber, string State,
    bool ReviewWillBeCancelled, IReadOnlyList<string> Requirements);

/// <summary>A procedure whose coverage returns to earlier wording, or which is left covering nothing at all.</summary>
public sealed record DisturbedCoverage(string Procedure, string Requirement, string Consequence);

/// <summary>
/// Everything reopening a baseline will do, in the words the confirmation shows.
///
/// Computed by the same pass that performs the reopen, so what a reader is asked to approve and what happens
/// cannot describe different things.
/// </summary>
public sealed record ReopenConsequences(
    IReadOnlyList<string> RevisionsTakenBack,
    IReadOnlyList<string> RequirementsRemoved,
    IReadOnlyList<StrandedChangeRequest> StrandedChangeRequests,
    IReadOnlyList<DisturbedCoverage> DisturbedCoverage,
    int CodeRecordsTakenBack)
{
    public int RevisionCount => RevisionsTakenBack.Count;
    public static ReopenConsequences None { get; } = new([], [], [], [], 0);
}

/// <summary>
/// Takes back what materializing a baseline created, so a reopened build reads as open.
///
/// Freezing fixes what a build contains and materializing moves the requirements themselves. Reopening undoes
/// the first; without this it would leave the second standing, and the requirements would still read as though
/// the build were sealed while the build said otherwise.
///
/// Everything removed here is identified by the baseline that made it. A revision carries the baseline it was
/// materialized into, so "what did this baseline create" is a question the data answers rather than one that
/// has to be reconstructed. Nothing from an earlier baseline is touched: those revisions belong to builds that
/// have already been sealed, and in most cases released.
///
/// Two kinds of thing hang off a revision, and they are treated differently. What materializing created is
/// simply unmade -- coverage it carried forward, the authoring profile it wrote, the traces it drew. What was
/// written afterwards cannot be unmade, because nobody asked for it to exist and taking it away would destroy
/// somebody's work: a procedure written against this build's wording, a change request numbered onto it. Those
/// are moved back to what survives and flagged, so the reopen leaves a record of what it disturbed rather than
/// a set of rows pointing at revisions that are gone.
/// </summary>
public sealed class RequirementBaselineDematerializer(AeroLinkDbContext db)
{
    /// <summary>What the reopen would do, computed without doing any of it.</summary>
    public async Task<ReopenConsequences> PreviewAsync(Guid baselineId, string baselineDisplayNumber, CancellationToken ct)
        => (await PlanAsync(baselineId, baselineDisplayNumber, ct)).Consequences;

    /// <summary>Does it, and returns the same description the preview would have given.</summary>
    public async Task<ReopenConsequences> DematerializeAsync(Guid baselineId, string actorId,
        string baselineDisplayNumber, DateTimeOffset now, CancellationToken ct)
    {
        var plan = await PlanAsync(baselineId, baselineDisplayNumber, ct);
        if (plan.Revisions.Count == 0) return ReopenConsequences.None;

        foreach (var (link, ontoRevisionId, reason) in plan.CoverageToMove)
        {
            var moved = new TestRequirementCoverage(link.ProcedureRevisionId, ontoRevisionId);
            moved.MarkSuspect(reason, now);
            db.TestCoverage.Add(moved);
        }
        foreach (var stranded in plan.ChangeRequestsToStrand)
            stranded.ChangeRequest.StrandByReopenedBaseline(actorId, baselineDisplayNumber, stranded.Requirements, now);

        db.TestCoverage.RemoveRange(plan.Coverage);
        db.RequirementRevisionProfiles.RemoveRange(plan.Profiles);
        db.RequirementTraces.RemoveRange(plan.Traces);
        db.CodeTraceabilityRecords.RemoveRange(plan.CodeRecords);
        db.BaselineRequirements.RemoveRange(plan.Selections);
        db.RequirementRevisions.RemoveRange(plan.Revisions);
        db.SpecificationNodes.RemoveRange(plan.Placements);
        db.Requirements.RemoveRange(plan.OrphanedArtifacts);
        return plan.Consequences;
    }

    private sealed record StrandPlan(SystemChangeRequest ChangeRequest, IReadOnlyList<string> Requirements);

    private sealed record Plan(
        List<RequirementRevision> Revisions,
        List<RequirementRevisionProfile> Profiles,
        List<RequirementTraceLink> Traces,
        List<TestRequirementCoverage> Coverage,
        List<(TestRequirementCoverage Link, Guid OntoRevisionId, string Reason)> CoverageToMove,
        List<CodeTraceabilityRecord> CodeRecords,
        List<BaselineRequirementSelection> Selections,
        List<SpecificationNode> Placements,
        List<RequirementArtifact> OrphanedArtifacts,
        List<StrandPlan> ChangeRequestsToStrand,
        ReopenConsequences Consequences);

    private async Task<Plan> PlanAsync(Guid baselineId, string baselineDisplayNumber, CancellationToken ct)
    {
        var revisions = await db.RequirementRevisions.Where(x => x.EffectiveBaselineId == baselineId).ToListAsync(ct);
        if (revisions.Count == 0)
            return new Plan([], [], [], [], [], [], [], [], [], [], ReopenConsequences.None);

        var revisionIds = revisions.Select(x => x.Id).ToList();
        var going = revisionIds.ToHashSet();
        var artifactIds = revisions.Select(x => x.ArtifactId).Distinct().ToList();
        var artifacts = await db.Requirements.Where(x => artifactIds.Contains(x.Id)).ToListAsync(ct);
        var artifactById = artifacts.ToDictionary(x => x.Id);

        // What each touched requirement falls back to. A requirement that existed before this build returns to
        // its newest earlier revision; one this build introduced has nothing to fall back to and ceases to
        // exist, which is what makes it the harder case for everything pointing at it.
        var earlier = await db.RequirementRevisions.AsNoTracking()
            .Where(x => artifactIds.Contains(x.ArtifactId) && x.EffectiveBaselineId != baselineId)
            .ToListAsync(ct);
        var fallback = earlier.GroupBy(x => x.ArtifactId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.Revision).First());
        var orphanedArtifacts = artifacts.Where(x => !fallback.ContainsKey(x.Id)).ToList();

        // Trace links are identified by the revisions they join. A link into a revision that is going away
        // would otherwise point at nothing, and a dangling trace is worse than an absent one because it reads
        // as coverage.
        var traces = await db.RequirementTraces
            .Where(x => revisionIds.Contains(x.SourceRevisionId) || revisionIds.Contains(x.TargetRevisionId))
            .ToListAsync(ct);
        var profiles = await db.RequirementRevisionProfiles
            .Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct);
        // Recorded against a revision that is about to stop existing, so it cannot survive in any form that
        // still means what it said. Counted rather than dropped quietly: the preview says how many, because a
        // reader deciding whether to reopen should know code was already written against this wording.
        var codeRecords = await db.CodeTraceabilityRecords
            .Where(x => revisionIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
        // Every selection naming one of these revisions, not only this baseline's own. A selection is what a
        // build says it contains, and one left naming a revision that is gone would be a build describing
        // something that does not exist.
        var selections = await db.BaselineRequirements
            .Where(x => x.BaselineId == baselineId || revisionIds.Contains(x.RevisionId)).ToListAsync(ct);
        var orphanedIds = orphanedArtifacts.Select(x => x.Id).ToList();
        var placements = orphanedIds.Count == 0
            ? []
            : await db.SpecificationNodes
                .Where(x => x.RequirementArtifactId != null && orphanedIds.Contains(x.RequirementArtifactId.Value))
                .ToListAsync(ct);

        var (coverage, toMove, disturbed) = await PlanCoverageAsync(baselineDisplayNumber, revisions, going, fallback, artifactById, ct);
        var stranded = await PlanStrandedAsync(revisions, artifactById, fallback, ct);

        var consequences = new ReopenConsequences(
            revisions.OrderBy(x => artifactById[x.ArtifactId].BaseNumber).ThenBy(x => x.Revision)
                .Select(x => ArtifactNumber.Display(artifactById[x.ArtifactId].BaseNumber, x.Revision)).ToList(),
            orphanedArtifacts.Select(x => x.BaseNumber).Order().ToList(),
            stranded.Select(x => new StrandedChangeRequest(x.ChangeRequest.Id, x.ChangeRequest.DisplayNumber,
                x.ChangeRequest.State.ToString(), x.ChangeRequest.State == ChangeRequestState.InReview,
                x.Requirements)).ToList(),
            disturbed,
            codeRecords.Count);

        return new Plan(revisions, profiles, traces, coverage, toMove, codeRecords, selections, placements,
            orphanedArtifacts, stranded, consequences);
    }

    /// <summary>
    /// What happens to a procedure written against wording that is being taken back.
    ///
    /// Three cases, and only the third is a consequence worth telling anybody about. A link this build carried
    /// forward is simply removed, because the link it was copied from is still there and untouched -- removing
    /// the copy restores exactly what was true before the build. A link created afterwards, by a test engineer
    /// binding a procedure to this build's wording, has no earlier copy to fall back on, so it is moved onto
    /// the revision the requirement returns to and marked suspect: the procedure was written against wording
    /// that no longer exists and its continued validity is unproven, which is precisely what suspect means. And
    /// where the requirement itself ceases to exist there is nothing left to cover, so the link goes and the
    /// procedure is named as covering nothing.
    /// </summary>
    private async Task<(List<TestRequirementCoverage>, List<(TestRequirementCoverage, Guid, string)>, List<DisturbedCoverage>)>
        PlanCoverageAsync(string baselineNumber, List<RequirementRevision> revisions, HashSet<Guid> going,
            Dictionary<Guid, RequirementRevision> fallback, Dictionary<Guid, RequirementArtifact> artifactById,
            CancellationToken ct)
    {
        var revisionIds = going.ToList();
        var coverage = await db.TestCoverage.Where(x => revisionIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
        if (coverage.Count == 0) return ([], [], []);

        var artifactByRevision = revisions.ToDictionary(x => x.Id, x => x.ArtifactId);
        var procedureRevisionIds = coverage.Select(x => x.ProcedureRevisionId).Distinct().ToList();
        // What each affected procedure will be linked to once this is done: what it already covers outside
        // the revisions going away, plus whatever gets moved back onto them below. A link that already has an
        // earlier counterpart is therefore recognised as a copy rather than moved onto one that exists.
        var linked = (await db.TestCoverage.AsNoTracking()
                .Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId) && !revisionIds.Contains(x.RequirementRevisionId))
                .Select(x => new { x.ProcedureRevisionId, x.RequirementRevisionId }).ToListAsync(ct))
            .Select(x => (x.ProcedureRevisionId, x.RequirementRevisionId)).ToHashSet();
        var procedureNumbers = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                      join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                                      where procedureRevisionIds.Contains(revision.Id)
                                      select new { revision.Id, procedure.BaseNumber, revision.Revision })
            .ToDictionaryAsync(x => x.Id, x => ArtifactNumber.Display(x.BaseNumber, x.Revision), ct);

        var toMove = new List<(TestRequirementCoverage, Guid, string)>();
        var disturbed = new List<DisturbedCoverage>();
        foreach (var link in coverage.OrderBy(x => procedureNumbers.GetValueOrDefault(x.ProcedureRevisionId, "")))
        {
            var artifactId = artifactByRevision[link.RequirementRevisionId];
            var procedure = procedureNumbers.GetValueOrDefault(link.ProcedureRevisionId, "a test procedure");
            if (!fallback.TryGetValue(artifactId, out var onto))
            {
                disturbed.Add(new DisturbedCoverage(procedure, artifactById[artifactId].BaseNumber,
                    $"Left covering nothing: {artifactById[artifactId].BaseNumber} was introduced by {baselineNumber} and ceases to exist."));
                continue;
            }
            // Once per destination, not once per link. Two change requests in one build can both modify the
            // same requirement, which materializes two revisions of it and can leave one procedure linked to
            // both; they fall back to the same surviving revision, and a second link to it would collide with
            // the uniqueness the coverage table keeps on (procedure revision, requirement revision).
            if (!linked.Add((link.ProcedureRevisionId, onto.Id))) continue;

            var restored = ArtifactNumber.Display(artifactById[artifactId].BaseNumber, onto.Revision);
            toMove.Add((link, onto.Id,
                $"{baselineNumber} was reopened and the revision this procedure was written against was taken back. "
                + $"It covers {restored} again, which says something different."));
            disturbed.Add(new DisturbedCoverage(procedure, restored,
                $"Returns to {restored} and is marked suspect until somebody confirms it still verifies it."));
        }
        return (coverage, toMove, disturbed);
    }

    /// <summary>
    /// Which change requests were left pointing at wording that is going away.
    ///
    /// A change request in a build is not among them: it is selected rather than pending, and the build it is
    /// selected into is the one being reopened. What this looks for is work written against this build's
    /// result -- a modification numbered onto a revision that will not exist, or any change at all to a
    /// requirement the reopen removes. An introduction of a removed requirement is deliberately not stranded:
    /// once the requirement is gone, introducing it is exactly what that change request proposes to do.
    /// </summary>
    private async Task<List<StrandPlan>> PlanStrandedAsync(List<RequirementRevision> revisions,
        Dictionary<Guid, RequirementArtifact> artifactById, Dictionary<Guid, RequirementRevision> fallback,
        CancellationToken ct)
    {
        var projectId = artifactById.Values.Select(x => x.ProjectId).First();
        var numbers = revisions.Select(x => artifactById[x.ArtifactId].BaseNumber).Distinct().ToList();
        var byNumber = artifactById.Values.DistinctBy(x => x.BaseNumber).ToDictionary(x => x.BaseNumber, x => x.Id);

        // The same graph `IChangeRequestRepository.GetAsync` loads, and for the same reason. Stranding one of
        // these cancels its review cycle, and cancelling publishes whatever draft comments its reviewers left
        // -- against a collection that is empty unless it was loaded, so the write would succeed, nothing
        // would error, and a reviewer's writing would simply never appear.
        var pending = await db.SystemChangeRequests
            .Include(x => x.RequirementChanges)
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Comments)
            .Include(x => x.AuditEvents)
            .Where(x => x.ProjectId == projectId
                && (x.State == ChangeRequestState.Draft || x.State == ChangeRequestState.InReview)
                && x.RequirementChanges.Any(change => numbers.Contains(change.BaseNumber)))
            .ToListAsync(ct);

        var stranded = new List<StrandPlan>();
        foreach (var scr in pending.OrderBy(x => x.DisplayNumber))
        {
            var left = new List<string>();
            foreach (var change in scr.RequirementChanges.Where(x => byNumber.ContainsKey(x.BaseNumber)))
            {
                var artifactId = byNumber[change.BaseNumber];
                if (!fallback.TryGetValue(artifactId, out var onto))
                {
                    if (change.Kind != RequirementChangeKind.Introduce) left.Add(change.BaseNumber);
                    continue;
                }
                // Numbered past what will survive: the revision it was written against is one of the ones
                // going, so the next revision is no longer the one it claims to be.
                if (change.Revision > onto.Revision + 1) left.Add(change.BaseNumber);
            }
            if (left.Count > 0) stranded.Add(new StrandPlan(scr, left.Distinct().Order().ToList()));
        }
        return stranded;
    }
}
