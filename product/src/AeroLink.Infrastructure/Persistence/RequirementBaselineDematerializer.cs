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
public sealed class RequirementBaselineDematerializer(AeroLinkDbContext db, VerificationImpactService verificationImpact)
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

        // A carried suspect link and its lifecycle evidence were created by this candidate materialization.
        // Remove that aggregate together so reopening cannot leave an event referring to a deleted exact link.
        // Historical links/lifecycles are not in plan.Traces and therefore remain untouched.
        var transientTraceIds = plan.Traces.Select(x => x.Id).ToList();
        var transientLifecycles = await db.ExactLinkSuspectLifecycles
            .Where(x => x.LinkKind == ExactLinkKind.RequirementTrace && transientTraceIds.Contains(x.LinkId)).ToListAsync(ct);
        var transientLifecycleIds = transientLifecycles.Select(x => x.Id).ToList();
        var transientEvents = await db.ExactLinkSuspectEvents
            .Where(x => transientLifecycleIds.Contains(x.LifecycleId)).ToListAsync(ct);
        db.RequirementTraces.RemoveRange(plan.Traces);
        db.ExactLinkSuspectEvents.RemoveRange(transientEvents);
        db.ExactLinkSuspectLifecycles.RemoveRange(transientLifecycles);
        db.TestCoverage.RemoveRange(plan.Coverage);
        db.RequirementRevisionProfiles.RemoveRange(plan.Profiles);
        db.CodeTraceabilityRecords.RemoveRange(plan.CodeRecords);
        db.BaselineRequirements.RemoveRange(plan.Selections);
        db.RequirementRevisions.RemoveRange(plan.Revisions);
        db.SpecificationNodes.RemoveRange(plan.Placements);
        db.Requirements.RemoveRange(plan.OrphanedArtifacts);

        // Raised last, once the removals are staged, so the work describes the build as it will be rather
        // than as it was. A procedure covering nothing is the same finding a retirement produces, so it goes
        // to the same queue by the same route -- carrying the baseline that caused it, because a reopen is
        // somebody deciding about the build rather than the change request deciding anything.
        foreach (var group in plan.OrphanedProcedures.GroupBy(x => x.SourceChangeRequestId))
            await verificationImpact.RaiseProceduresOrphanedByReopenAsync(plan.ProjectId, plan.ReleaseId,
                baselineId, group.Key,
                group.Select(x => new VerificationImpactService.OrphanedProcedure(x.ProcedureId, x.DisplayNumber, x.Level)).ToList(),
                now, ct);
        return plan.Consequences;
    }

    private sealed record StrandPlan(SystemChangeRequest ChangeRequest, IReadOnlyList<string> Requirements);

    /// <summary>A procedure the reopen leaves covering nothing, and the change request whose work removed it.</summary>
    private sealed record OrphanedProcedureRef(Guid ProcedureId, string DisplayNumber, TestProcedureLevel Level,
        Guid SourceChangeRequestId);

    private sealed record CoveragePlan(
        List<TestRequirementCoverage> Coverage,
        List<(TestRequirementCoverage Link, Guid OntoRevisionId, string Reason)> ToMove,
        List<DisturbedCoverage> Disturbed,
        List<OrphanedProcedureRef> Orphaned);

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
        List<OrphanedProcedureRef> OrphanedProcedures,
        Guid ProjectId,
        Guid ReleaseId,
        ReopenConsequences Consequences);

    private async Task<Plan> PlanAsync(Guid baselineId, string baselineDisplayNumber, CancellationToken ct)
    {
        var revisions = await db.RequirementRevisions.Where(x => x.EffectiveBaselineId == baselineId).ToListAsync(ct);
        if (revisions.Count == 0)
            return new Plan([], [], [], [], [], [], [], [], [], [], [], Guid.Empty, Guid.Empty, ReopenConsequences.None);

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

        var coveragePlan = await PlanCoverageAsync(baselineDisplayNumber, revisions, going, fallback, artifactById, ct);
        var stranded = await PlanStrandedAsync(revisions, artifactById, fallback, ct);

        var consequences = new ReopenConsequences(
            revisions.OrderBy(x => artifactById[x.ArtifactId].BaseNumber).ThenBy(x => x.Revision)
                .Select(x => ArtifactNumber.Display(artifactById[x.ArtifactId].BaseNumber, x.Revision)).ToList(),
            orphanedArtifacts.Select(x => x.BaseNumber).Order().ToList(),
            stranded.Select(x => new StrandedChangeRequest(x.ChangeRequest.Id, x.ChangeRequest.DisplayNumber,
                x.ChangeRequest.State.ToString(), x.ChangeRequest.State == ChangeRequestState.InReview,
                x.Requirements)).ToList(),
            coveragePlan.Disturbed,
            codeRecords.Count);

        var projectId = artifactById.Values.Select(x => x.ProjectId).First();
        var releaseId = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.Id == baselineId).Select(x => x.ReleaseId).SingleAsync(ct);
        return new Plan(revisions, profiles, traces, coveragePlan.Coverage, coveragePlan.ToMove, codeRecords,
            selections, placements, orphanedArtifacts, stranded, coveragePlan.Orphaned, projectId, releaseId,
            consequences);
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
    private async Task<CoveragePlan> PlanCoverageAsync(string baselineNumber, List<RequirementRevision> revisions,
        HashSet<Guid> going, Dictionary<Guid, RequirementRevision> fallback,
        Dictionary<Guid, RequirementArtifact> artifactById, CancellationToken ct)
    {
        var revisionIds = going.ToList();
        var coverage = await db.TestCoverage.Where(x => revisionIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
        if (coverage.Count == 0) return new CoveragePlan([], [], [], []);

        var revisionById = revisions.ToDictionary(x => x.Id);
        var procedureRevisionIds = coverage.Select(x => x.ProcedureRevisionId).Distinct().ToList();
        // What each affected procedure will be linked to once this is done: what it already covers outside
        // the revisions going away, plus whatever gets moved back onto them below. A link that already has an
        // earlier counterpart is therefore recognised as a copy rather than moved onto one that exists.
        var linked = (await db.TestCoverage.AsNoTracking()
                .Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId) && !revisionIds.Contains(x.RequirementRevisionId))
                .Select(x => new { x.ProcedureRevisionId, x.RequirementRevisionId }).ToListAsync(ct))
            .Select(x => (x.ProcedureRevisionId, x.RequirementRevisionId)).ToHashSet();
        // Which procedures still verify something once this is done. Seeded from what they cover outside this
        // baseline and added to as coverage is moved back, because a procedure that ends up covering earlier
        // wording is not orphaned -- it is suspect, which is a different finding with a different remedy.
        var stillCovers = linked.Select(x => x.ProcedureRevisionId).ToHashSet();
        var procedures = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                                where procedureRevisionIds.Contains(revision.Id)
                                select new { RevisionId = revision.Id, procedure.Id, procedure.BaseNumber, procedure.Level, revision.Revision })
            .ToDictionaryAsync(x => x.RevisionId, ct);
        string Name(Guid procedureRevisionId) => procedures.TryGetValue(procedureRevisionId, out var row)
            ? ArtifactNumber.Display(row.BaseNumber, row.Revision)
            : "a test procedure";

        var toMove = new List<(TestRequirementCoverage, Guid, string)>();
        var disturbed = new List<DisturbedCoverage>();
        var candidates = new List<TestRequirementCoverage>();
        foreach (var link in coverage.OrderBy(x => Name(x.ProcedureRevisionId)))
        {
            var artifactId = revisionById[link.RequirementRevisionId].ArtifactId;
            if (!fallback.TryGetValue(artifactId, out var onto)) { candidates.Add(link); continue; }

            // Once per destination, not once per link. Two change requests in one build can both modify the
            // same requirement, which materializes two revisions of it and can leave one procedure linked to
            // both; they fall back to the same surviving revision, and a second link to it would collide with
            // the uniqueness the coverage table keeps on (procedure revision, requirement revision).
            if (!linked.Add((link.ProcedureRevisionId, onto.Id))) continue;
            stillCovers.Add(link.ProcedureRevisionId);

            var restored = ArtifactNumber.Display(artifactById[artifactId].BaseNumber, onto.Revision);
            toMove.Add((link, onto.Id,
                $"{baselineNumber} was reopened and the revision this procedure was written against was taken back. "
                + $"It covers {restored} again, which says something different."));
            disturbed.Add(new DisturbedCoverage(Name(link.ProcedureRevisionId), restored,
                $"Returns to {restored} and is marked suspect until somebody confirms it still verifies it."));
        }

        // Settled only after every move is known: a procedure linked to two of these revisions can lose one to
        // a requirement that ceases to exist and keep the other, and asking mid-loop would have called it
        // orphaned on the strength of whichever link happened to be read first.
        var orphaned = new List<OrphanedProcedureRef>();
        var named = new HashSet<Guid>();
        foreach (var link in candidates)
        {
            var artifact = artifactById[revisionById[link.RequirementRevisionId].ArtifactId];
            if (stillCovers.Contains(link.ProcedureRevisionId)) continue;
            if (!procedures.TryGetValue(link.ProcedureRevisionId, out var row)) continue;
            if (!named.Add(row.Id)) continue;
            disturbed.Add(new DisturbedCoverage(Name(link.ProcedureRevisionId), artifact.BaseNumber,
                $"Left covering nothing: {artifact.BaseNumber} was introduced by {baselineNumber} and ceases to exist. "
                + "It becomes verification work rather than being left in the library covering no requirement."));
            orphaned.Add(new OrphanedProcedureRef(row.Id, row.BaseNumber, row.Level,
                revisionById[link.RequirementRevisionId].SourceChangeRequestId!.Value));
        }
        return new CoveragePlan(coverage, toMove, disturbed, orphaned);
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
