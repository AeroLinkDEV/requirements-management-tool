using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// One requirement a proposal allocates to, downstream of the revision the proposal changes.
///
/// <paramref name="IsProposed"/> separates a requirement that exists from one that is only proposed in another
/// change request. Both genuinely allocate to the same parent, but only one of them is in the build today, and
/// a lane that drew them identically would say the coverage is real when half of it is still under review.
/// </summary>
public sealed record ProposalAllocationTarget(
    /// <summary>
    /// The record's own identity: the controlled artifact for a materialized target, the proposal row for one
    /// that is only proposed. Two different kinds of thing, which is why <see cref="IsProposed"/> exists.
    /// </summary>
    Guid Id,
    string DisplayNumber,
    string Level,
    string Statement,
    bool IsProposed,
    /// <summary>
    /// The exact revision this target is at, for a materialized one. Null for a proposal, which has no
    /// controlled revision yet.
    ///
    /// Carried separately from <see cref="Id"/> because the two answer different questions and are not
    /// interchangeable. Verification coverage is keyed by requirement <em>revision</em>, so a consumer given
    /// only the artifact id would have to re-resolve a display number or collapse every revision of the
    /// artifact into one — both of which would state a relationship the record does not hold.
    /// </summary>
    Guid? RevisionId = null,
    string? LinkType = null,
    Guid? ChangeRequestId = null,
    string? ChangeRequestDisplayNumber = null);

/// <summary>
/// One verification artifact covering an exact requirement revision, for lane 3 of the inside-a-change view.
///
/// <paramref name="CoverageState"/> is the server's, from the single coverage definition the release gate and
/// the requirements workspace already read. A product that answers "is this covered?" in two places must not
/// answer it two ways, and the browser must never decide it at all.
/// </summary>
public sealed record ProposalCoveringArtifact(
    Guid RequirementRevisionId,
    Guid ArtifactId,
    Guid ArtifactRevisionId,
    string DisplayNumber,
    string Title,
    string Level,
    string ArtifactKind,
    string ArtifactState,
    string CoverageState);

/// <summary>
/// One baseline this change's content sits in, for lane 4 — the effect on the build.
///
/// Real candidate/predecessor baseline records, not labels assembled in the browser.
/// <paramref name="IsPredecessor"/> separates the baseline being built from the one it supersedes.
/// </summary>
public sealed record ProposalBaselineEffect(
    Guid BaselineId,
    string DisplayNumber,
    string Name,
    string State,
    bool IsPredecessor);

/// <summary>
/// One proposed item, with the text it supersedes and what allocates below it.
///
/// <paramref name="SupersededStatement"/> is null rather than empty when there is nothing to show, and the two
/// are not interchangeable. An Introduce supersedes nothing; a Modify whose base revision cannot be resolved is
/// a gap in the record. Either way a null says "no before text exists", where an empty string would render as a
/// diff from nothing and assert that the author wrote every word of the statement afresh.
/// </summary>
public sealed record ChangeProposalItem(
    Guid Id,
    string DisplayNumber,
    string Level,
    string Kind,
    string Statement,
    string? SupersededStatement,
    int? SupersededRevision,
    Guid? BaseRevisionId,
    IReadOnlyList<ProposalAllocationTarget> AllocatedDownstream,
    /// <summary>Why <see cref="AllocatedDownstream"/> is empty, when it is. See the enum.</summary>
    ProposalDownstreamDisposition Disposition = ProposalDownstreamDisposition.Allocated,
    /// <summary>
    /// The highest revision number this base number has in the Project, when the base number is known.
    ///
    /// A number, not a lifecycle judgement. It is paired with <see cref="LatestRevisionState"/> because the
    /// highest revision of a retired requirement is Retired, and a bare maximum would let a reader infer the
    /// requirement is live when it is not.
    /// </summary>
    int? LatestRevision = null,
    /// <summary>The state of <see cref="LatestRevision"/>: Active, Superseded or Retired.</summary>
    string? LatestRevisionState = null);

/// <summary>
/// Why a proposed item has nothing allocated below it, decided from the record rather than guessed.
///
/// These are five different facts and the browser must not have to tell them apart from a null and a
/// change-request-wide flag. In particular <see cref="BehindTarget"/> and <see cref="BaseRevisionUnresolved"/>
/// are not interchangeable: the first says a later revision of this requirement exists and the allocation
/// hangs off that, which is a claim about traceability; the second says the named base could not be resolved
/// at all, which is a gap in the record and claims nothing. Presenting a gap as staleness would assert a
/// relationship nobody recorded.
/// </summary>
public enum ProposalDownstreamDisposition
{
    /// <summary>Something is allocated below this item.</summary>
    Allocated,
    /// <summary>An Introduce: nothing can allocate to a requirement the build does not have yet.</summary>
    TargetNotYetCreated,
    /// <summary>The exact base revision resolved and genuinely has nothing below it.</summary>
    NoAllocationRecorded,
    /// <summary>
    /// The proposal names an older revision than the Project now holds.
    ///
    /// That is exactly what this claims and no more. It does <b>not</b> say an allocation exists on the later
    /// revision — nothing here looked there, and saying "what is allocated hangs off the later revision" would
    /// assert a relationship that may not exist at all. The reader is told which revision the proposal targets
    /// and which the requirement has reached; that is a fact, and it is enough to explain the empty lane.
    ///
    /// Decided per item by comparing revisions, never from the change request's overall rebase flag: one
    /// stale item strands the whole change request, and its siblings are not thereby stale.
    /// </summary>
    BehindTarget,
    /// <summary>The named base number and revision resolved to no revision at all. A data gap, not staleness.</summary>
    BaseRevisionUnresolved,
}

/// <summary>The proposed content of one change request: lane 1 and lane 2 of the Digital Thread's inside-a-change view.</summary>
public sealed record ChangeProposalContentResult(
    /// <summary>
    /// Which aggregate this content came from, so the client can hold the two proposal shapes as a
    /// discriminated union rather than one shape whose fields mean different things depending on the owner.
    /// Matches the node kinds the trace projection already uses.
    /// </summary>
    string OwnerKind,
    Guid ChangeRequestId,
    Guid ProjectId,
    string DisplayNumber,
    IReadOnlyList<ChangeProposalItem> Items,
    /// <summary>Lane 3: what covers the requirement revisions this change allocates to.</summary>
    IReadOnlyList<ProposalCoveringArtifact> Covering,
    /// <summary>Lane 4: the candidate baseline this content sits in, and the one it supersedes.</summary>
    IReadOnlyList<ProposalBaselineEffect> BuildEffect);

/// <summary>
/// Reads what a change request proposes, resolved at the revision the proposal was actually written against.
///
/// This exists because the two facts the inside-a-change view needs are not on the change record. A
/// <see cref="RequirementChange"/> carries the proposed statement and the revision it supersedes, but not that
/// revision's text, and nothing reads downward from a proposal at all. `/api/authoring/impact` answers a similar
/// question for authoring, but anchors to the requirement's *latest* revision, which is the wrong anchor here:
/// a change written against Build 1.5 and read during Build 1.6 would be diffed against text that was never its
/// baseline, and the view would make a false statement about what the change altered.
/// </summary>
public static class ChangeProposalContentProjection
{
    private sealed record BaseRevision(string BaseNumber, int Revision, Guid Id, string Statement, string State);

    private sealed record MaterializedChild(
        Guid TargetRevisionId, Guid Id, Guid RevisionId, string BaseNumber, int Revision, string Level,
        string Statement, string Type);

    private sealed record ProposedChild(
        Guid Id, string BaseNumber, int Revision, string Level, string Statement,
        string ProposedUpstreamRevisionIdsJson, Guid OwnerId, string OwnerNumber, int OwnerRevision);

    public static async Task<ChangeProposalContentResult?> ForChangeRequestAsync(
        AeroLinkDbContext db, Guid projectId, Guid changeRequestId, CancellationToken ct)
    {
        var scr = await db.SystemChangeRequests.AsNoTracking()
            .Include(x => x.RequirementChanges)
            .SingleOrDefaultAsync(x => x.Id == changeRequestId && x.ProjectId == projectId, ct);
        if (scr is null) return null;

        var changes = scr.RequirementChanges.OrderBy(x => x.DisplayNumber, StringComparer.Ordinal).ToList();

        // The base revisions every Modify and Retire in this change request points at, resolved in one pass.
        // A proposal names its target as (base number, revision); the pair is the exact superseded revision,
        // already pinned by authoring and moved deliberately by a rebase, so no build lookup is needed or
        // wanted — the record is more precise than the build would be.
        var baseNumbers = changes
            .Where(x => x.Kind != RequirementChangeKind.Introduce && !string.IsNullOrWhiteSpace(x.BaseNumber))
            .Select(x => x.BaseNumber)
            .Distinct()
            .ToList();

        var byBase = new Dictionary<(string, int), BaseRevision>();
        if (baseNumbers.Count > 0)
        {
            var rows = await (from artifact in db.Requirements.AsNoTracking()
                              where artifact.ProjectId == projectId && baseNumbers.Contains(artifact.BaseNumber)
                              join revision in db.RequirementRevisions.AsNoTracking()
                                  on artifact.Id equals revision.ArtifactId
                              select new BaseRevision(
                                  artifact.BaseNumber, revision.Revision, revision.Id, revision.Statement,
                                  revision.State.ToString()))
                             .ToListAsync(ct);
            foreach (var row in rows) byBase[(row.BaseNumber, row.Revision)] = row;
        }

        // The newest revision of each named base number. This is what makes "behind its target" provable for a
        // single item: the proposal names revision N and the Project already holds N+1, so whatever is allocated
        // hangs off the later one. Nothing here consults the change request's overall rebase flag, which says
        // only that *some* item stranded it.
        var latestByBase = byBase.Values
            .GroupBy(x => x.BaseNumber)
            .ToDictionary(
                x => x.Key,
                x => x.OrderByDescending(y => y.Revision).First());

        var revisionIds = byBase.Values.Select(x => x.Id).ToList();

        var materialized = new List<MaterializedChild>();
        var proposedChildren = new List<ProposedChild>();
        if (revisionIds.Count > 0)
        {
            // What allocates downward from those revisions, as recorded. A trace link points child -> parent,
            // so the children of a base revision are the links whose target it is.
            materialized = await (from link in db.RequirementTraces.AsNoTracking()
                                  where link.ProjectId == projectId && revisionIds.Contains(link.TargetRevisionId)
                                  join revision in db.RequirementRevisions.AsNoTracking()
                                      on link.SourceRevisionId equals revision.Id
                                  join child in db.Requirements.AsNoTracking()
                                      on revision.ArtifactId equals child.Id
                                  select new MaterializedChild(
                                      link.TargetRevisionId,
                                      child.Id,
                                      revision.Id,
                                      child.BaseNumber,
                                      revision.Revision,
                                      child.Level.ToString(),
                                      revision.Statement,
                                      link.Type.ToString()))
                                 .ToListAsync(ct);

            // Children that are themselves only proposed. A proposal points up at real revision identifiers, so
            // a proposed child of this change's base revision is discoverable, but only within the build being
            // read: the upstream list is JSON and cannot be filtered in the database, so the candidate set is
            // bounded by the release rather than scanning every change request in the Project.
            proposedChildren = await (from change in db.RequirementChanges.AsNoTracking()
                                      join owner in db.SystemChangeRequests.AsNoTracking()
                                          on change.ChangeRequestId equals owner.Id
                                      where owner.ProjectId == projectId
                                          && owner.TargetReleaseId == scr.TargetReleaseId
                                          && owner.Id != scr.Id
                                      select new ProposedChild(
                                          change.Id,
                                          change.BaseNumber,
                                          change.Revision,
                                          change.Level.ToString(),
                                          change.Statement,
                                          change.ProposedUpstreamRevisionIdsJson,
                                          owner.Id,
                                          owner.BaseNumber,
                                          owner.Revision))
                                     .ToListAsync(ct);
        }

        var items = new List<ChangeProposalItem>(changes.Count);
        foreach (var change in changes)
        {
            BaseRevision? resolved = null;
            if (change.Kind != RequirementChangeKind.Introduce && !string.IsNullOrWhiteSpace(change.BaseNumber))
                byBase.TryGetValue((change.BaseNumber, change.Revision), out resolved);

            // Only a Modify shows a before/after. A Retire resolves its base revision all the same, because
            // what allocates below the thing being retired is exactly the cascade the view draws dashed.
            var superseded = resolved is not null && change.Kind == RequirementChangeKind.Modify
                ? resolved.Statement
                : null;

            var allocated = new List<ProposalAllocationTarget>();
            if (resolved is not null)
            {
                allocated.AddRange(materialized
                    .Where(x => x.TargetRevisionId == resolved.Id)
                    .Select(x => new ProposalAllocationTarget(
                        x.Id,
                        Display(x.BaseNumber, x.Revision),
                        x.Level,
                        x.Statement,
                        IsProposed: false,
                        RevisionId: x.RevisionId,
                        LinkType: x.Type)));

                allocated.AddRange(proposedChildren
                    .Where(x => Upstream(x.ProposedUpstreamRevisionIdsJson).Contains(resolved.Id))
                    .Select(x => new ProposalAllocationTarget(
                        x.Id,
                        string.IsNullOrWhiteSpace(x.BaseNumber) ? "" : Display(x.BaseNumber, x.Revision),
                        x.Level,
                        x.Statement,
                        IsProposed: true,
                        ChangeRequestId: x.OwnerId,
                        ChangeRequestDisplayNumber: Display(x.OwnerNumber, x.OwnerRevision))));
            }

            BaseRevision? newest = null;
            if (!string.IsNullOrWhiteSpace(change.BaseNumber))
                latestByBase.TryGetValue(change.BaseNumber, out newest);
            var latest = newest?.Revision;

            var disposition = allocated.Count > 0
                ? ProposalDownstreamDisposition.Allocated
                : change.Kind == RequirementChangeKind.Introduce
                    ? ProposalDownstreamDisposition.TargetNotYetCreated
                    : resolved is null
                        ? ProposalDownstreamDisposition.BaseRevisionUnresolved
                        : latest is int newestRevision && newestRevision > resolved.Revision
                            ? ProposalDownstreamDisposition.BehindTarget
                            : ProposalDownstreamDisposition.NoAllocationRecorded;

            items.Add(new ChangeProposalItem(
                change.Id,
                change.DisplayNumber,
                change.Level.ToString(),
                change.Kind.ToString(),
                change.Statement,
                superseded,
                resolved?.Revision,
                resolved?.Id,
                allocated.OrderBy(x => x.DisplayNumber, StringComparer.Ordinal).ToList(),
                disposition,
                latest,
                newest?.State));
        }

        // Lane 3: the single coverage definition, read for the exact revisions this change allocates to.
        // Reusing it rather than writing a second one is the point — the release gate and the requirements
        // workspace already answer "is this covered?" from here.
        var coveredRevisionIds = items
            .SelectMany(x => x.AllocatedDownstream)
            .Select(x => x.RevisionId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        var covering = await CoveringAsync(db, scr.ProjectId, coveredRevisionIds, ct);
        var effect = await BuildEffectAsync(db, scr.ProjectId, "ChangeRequest", scr.Id, ct);

        return new ChangeProposalContentResult("ChangeRequest", scr.Id, scr.ProjectId, scr.DisplayNumber,
            items, covering, effect);
    }

    private static string Display(string baseNumber, int revision)
        => $"{baseNumber}.{revision:D2}";

    /// <summary>
    /// The candidate baseline that actually selected this change, and the one it supersedes.
    ///
    /// Resolved from the explicit selection row — <c>BaselineChangeRequestSelection</c> for a change request,
    /// <c>BaselineTestChangeRequestSelection</c> for a verification package — and not from "the newest
    /// candidate for this release". Those are different claims: a release can hold several candidates, and
    /// picking the highest revision would show an opened change as affecting a baseline that never selected
    /// it. #880 asks for candidate-baseline <em>selection</em> state.
    ///
    /// No selection means an empty lane, which is the truthful answer. The predecessor is that selected
    /// baseline's own, not the release's.
    /// </summary>
    internal static async Task<IReadOnlyList<ProposalBaselineEffect>> BuildEffectAsync(
        AeroLinkDbContext db, Guid projectId, string ownerKind, Guid ownerId, CancellationToken ct)
    {
        var selectedBaselineIds = ownerKind == "TestChangeRequest"
            ? await db.BaselineTestChangeSelections.AsNoTracking()
                .Where(x => x.TestChangeRequestId == ownerId).Select(x => x.BaselineId).ToListAsync(ct)
            : await db.BaselineSelections.AsNoTracking()
                .Where(x => x.ChangeRequestId == ownerId).Select(x => x.BaselineId).ToListAsync(ct);
        if (selectedBaselineIds.Count == 0) return [];

        var selected = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.ProjectId == projectId && selectedBaselineIds.Contains(x.Id))
            .Select(x => new { x.Id, x.BaseNumber, x.Revision, x.Name, x.State, x.PredecessorBaselineId })
            .ToListAsync(ct);
        if (selected.Count == 0) return [];

        var effect = new List<ProposalBaselineEffect>();
        foreach (var baseline in selected.OrderBy(x => x.BaseNumber, StringComparer.Ordinal).ThenBy(x => x.Revision))
            effect.Add(new(baseline.Id, Display(baseline.BaseNumber, baseline.Revision), baseline.Name,
                baseline.State.ToString(), IsPredecessor: false));

        var predecessorIds = selected.Select(x => x.PredecessorBaselineId).OfType<Guid>().Distinct().ToList();
        if (predecessorIds.Count > 0)
        {
            var predecessors = await db.CandidateBaselines.AsNoTracking()
                .Where(x => x.ProjectId == projectId && predecessorIds.Contains(x.Id))
                .Select(x => new { x.Id, x.BaseNumber, x.Revision, x.Name, x.State })
                .ToListAsync(ct);
            foreach (var predecessor in predecessors.OrderBy(x => x.BaseNumber, StringComparer.Ordinal))
                effect.Add(new(predecessor.Id, Display(predecessor.BaseNumber, predecessor.Revision),
                    predecessor.Name, predecessor.State.ToString(), IsPredecessor: true));
        }

        return effect;
    }

    /// <summary>
    /// Verification coverage for these requirement revisions, constrained to the authorized Project.
    ///
    /// The reusable coverage projection joins procedure revisions by identity and does not itself constrain
    /// the joined artifact to a Project — <c>TestRequirementCoverage</c> carries revision ids, not a
    /// ProjectId. For ordinary data the identities line up, but this is exactly the seam §8.6 says must filter
    /// server-side: a malformed or imported coverage row pointing at another Project's verification revision
    /// would otherwise carry that Project's display number, title and state into this response.
    ///
    /// Scoped here rather than inside the shared projection so its other callers keep their semantics.
    /// </summary>
    private static async Task<IReadOnlyList<ProposalCoveringArtifact>> CoveringAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> requirementRevisionIds,
        CancellationToken ct)
    {
        if (requirementRevisionIds.Count == 0) return [];

        var links = await VerificationCoverageProjection.ForRequirementRevisionsAsync(
            db, requirementRevisionIds, ct);
        if (links.Count == 0) return [];

        var artifactIds = links.Select(x => x.ArtifactId).Distinct().ToList();
        var inProject = (await db.TestProcedures.AsNoTracking()
                .Where(x => x.ProjectId == projectId && artifactIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToListAsync(ct))
            .ToHashSet();

        return links
            .Where(x => inProject.Contains(x.ArtifactId))
            .Select(x => new ProposalCoveringArtifact(x.RequirementRevisionId, x.ArtifactId,
                x.ArtifactRevisionId, x.DisplayNumber, x.Title, x.Level, x.ArtifactKind, x.ArtifactState,
                x.CoverageState))
            .OrderBy(x => x.DisplayNumber, StringComparer.Ordinal)
            .ThenBy(x => x.RequirementRevisionId)
            .ToList();
    }

    private static IReadOnlyList<Guid> Upstream(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
