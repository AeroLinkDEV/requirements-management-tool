using static AeroLink.Infrastructure.Persistence.FrozenReviewTraceParser;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One exact node in the server-owned change-request trace projection.</summary>
public sealed record ChangeRequestTraceNode(
    Guid Id,
    string Kind,
    string DisplayNumber,
    string? Title,
    string? State,
    Guid? ProjectId,
    Guid? BuildId,
    string? BuildVersion,
    int? Revision,
    string? Level,
    Guid? ArtifactId = null,
    IReadOnlyList<Guid>? BaselineMembershipIds = null);

/// <summary>One provenance fact carried by a composed trace edge.</summary>
public sealed record ChangeRequestTraceProvenance(
    string Kind,
    Guid? SourceId,
    Guid? AssessmentId = null,
    Guid? AssessmentLinkId = null,
    Guid? ProcedureRevisionId = null,
    bool IsLive = true,
    string? Status = null,
    string? Rationale = null,
    string? ActorId = null,
    DateTimeOffset? StatedAt = null,
    Guid? UpstreamBuildId = null,
    string? UpstreamBuildVersion = null,
    Guid? BuildId = null,
    string? BuildVersion = null,
    Guid? ReopeningId = null,
    string? ReopeningReason = null,
    string? ReopenedBy = null,
    DateTimeOffset? ReopenedAt = null,
    string? PreviousState = null,
    string? PreviousOutcome = null);

/// <summary>
/// A typed edge in the composed trace. Change-request pairs are folded by exact identity, so a historical
/// pair that has both an authored and an assessment-derived fact is one edge with two provenance entries.
/// </summary>
public sealed record ChangeRequestTraceEdge(
    Guid FromId,
    string FromKind,
    Guid ToId,
    string ToKind,
    string Relation,
    IReadOnlyList<ChangeRequestTraceProvenance> Provenance,
    /// <summary>
    /// Whether this relationship is suspect, stated by the server from the exact-link suspect lifecycle.
    ///
    /// Served rather than left to the reader. A client deciding it by looking for the word "suspect" in the
    /// relation or a provenance status is reconstructing lifecycle meaning out of display vocabulary: it turns
    /// any wording that happens to contain the word into a suspect edge, and hides a genuinely suspect one
    /// whose wording does not. Suspect state and relation vocabulary are separate facts and stay separate.
    /// </summary>
    bool IsSuspect = false);

public sealed record ChangeRequestTraceState(
    string Upstream,
    string Downstream,
    string Overall,
    bool IsTopOfLadder,
    IReadOnlyList<string> Warnings);

public sealed record ChangeRequestTraceProjectionResult(
    Guid ProjectId,
    Guid RootChangeRequestId,
    IReadOnlyList<ChangeRequestTraceNode> Nodes,
    IReadOnlyList<ChangeRequestTraceEdge> Edges,
    ChangeRequestTraceState? State,
    Guid? RootArtifactId = null,
    string? RootArtifactKind = null,
    bool DirectOnly = false);

/// <summary>
/// The change-control network for one exact build: every change request and test change request targeting it,
/// every problem report feeding one of those, and the typed relations between them.
///
/// This is the same node and edge vocabulary as the rooted trace, scoped by build rather than by reachability
/// from a root. <see cref="Truncated"/> is true when the build carried more records than the caller's ceiling
/// allowed; a consumer must say so rather than presenting a shortened network as the whole one.
/// </summary>
public sealed record ChangeRequestNetworkResult(
    Guid ProjectId,
    Guid ReleaseId,
    IReadOnlyList<ChangeRequestTraceNode> Nodes,
    IReadOnlyList<ChangeRequestTraceEdge> Edges,
    bool Truncated,
    /// <summary>
    /// The Project's configured requirement ladder, highest layer first.
    ///
    /// A consumer laying records out by level must not decide that order for itself. Layers above System —
    /// Customer and Interface among them — are configured per Project, so a fixed client-side order would be
    /// wrong for any Project that configures them differently, and would need editing whenever a new layer is
    /// supported. The ladder policy already owns this; the projection states it.
    /// </summary>
    IReadOnlyList<string> OrderedLevels);

/// <summary>
/// Read authority for the Phase 2 composed change-request trace.
///
/// This is deliberately a domain-specific projection rather than a generic relationship graph. It reads the
/// existing exact stores (authored CR links, assessments, TCR source snapshots, Case origins, requirements and
/// code) in set-based batches, then performs the bounded, deterministic walk in memory. No client is allowed
/// to infer provenance or lifecycle meaning from a collection of unrelated API responses.
/// </summary>
public static partial class ChangeRequestTraceProjection
{
    private sealed record CrRow(Guid Id, Guid ProjectId, Guid TargetReleaseId, string BaseNumber, int Revision,
        string Title, ChangeRequestState State, ChangeRequestType Type, RequirementLevel? SoftwareLevel,
        string? NoUpstreamRationale, string? InheritedUpstreamContextJson, bool UpstreamAnswerAffirmed);
    private sealed record CrIdentity(Guid ProjectId, Guid TargetReleaseId, ChangeRequestType Type,
        RequirementLevel? SoftwareLevel, ChangeRequestState State);
    private sealed record PairKey(Guid ChildId, Guid ParentId);
    private sealed class EdgeBuilder(Guid fromId, string fromKind, Guid toId, string toKind, string relation,
        bool isSuspect = false)
    {
        public Guid FromId { get; } = fromId;
        public string FromKind { get; } = fromKind;
        public Guid ToId { get; } = toId;
        public string ToKind { get; } = toKind;
        public string Relation { get; } = relation;
        public bool IsSuspect { get; set; } = isSuspect;
        public List<ChangeRequestTraceProvenance> Provenance { get; } = [];
    }
    private static readonly TestChangeReviewOriginKind[] CaseOrigins =
        [TestChangeReviewOriginKind.CaseChange, TestChangeReviewOriginKind.CaseAssessment,
            TestChangeReviewOriginKind.CaseReview];

    /// <summary>Projects one exact CR after its caller has established Project access.</summary>
    public static async Task<ChangeRequestTraceProjectionResult?> ForChangeRequestAsync(
        AeroLinkDbContext db, Guid projectId, Guid rootChangeRequestId, ILadderPolicy policy,
        CancellationToken ct, bool directOnly = false)
    {
        var projection = await BuildAsync(db, projectId, rootChangeRequestId, "ChangeRequest", policy, ct, directOnly: directOnly);
        return projection;
    }

    /// <summary>Projects one exact Test Change Request root through the same composed, bounded graph.</summary>
    public static Task<ChangeRequestTraceProjectionResult?> ForTestChangeReviewAsync(
        AeroLinkDbContext db, Guid projectId, Guid rootTestChangeReviewId, ILadderPolicy policy,
        CancellationToken ct, bool directOnly = false) => BuildAsync(db, projectId, rootTestChangeReviewId, "TestChangeRequest", policy, ct, directOnly: directOnly);

    /// <summary>
    /// Projects the whole change-control network for one exact build in a single read.
    ///
    /// The rooted trace answers "what is this change connected to". This answers "what is in this build, and
    /// how is it connected", which no existing read covers and which a client must not assemble by calling the
    /// rooted trace once per change request. Membership is by exact target build for change and test-change
    /// requests, and by recorded link for problem reports, which have no build of their own on this surface.
    /// </summary>
    public static async Task<ChangeRequestNetworkResult> ForBuildAsync(
        AeroLinkDbContext db, Guid projectId, Guid releaseId, ILadderPolicy policy, int maxNodes,
        CancellationToken ct)
    {
        var ladder = policy.OrderedLevels.Select(x => x.ToString()).ToList();
        var ceiling = Math.Clamp(maxNodes, 1, TraceReadBudget.MaximumNodes);
        var budget = new TraceReadBudget();
        var scope = await SelectBuildAsync(db, projectId, releaseId, ceiling, budget, ct);
        var projection = await BuildAsync(db, projectId, Guid.Empty, NetworkRootKind, policy, ct, releaseId, scope, budget);
        if (projection is null) return new(projectId, releaseId, [], [], false, ladder);

        if (projection.Nodes.Count <= ceiling)
            return new(projectId, releaseId, projection.Nodes, projection.Edges, scope.Truncated, ladder);

        // Over the ceiling the network is cut deterministically and the cut is declared. Dropping records from
        // a traceability view without saying so would be a false statement about traceability, so the caller is
        // told; edges to a dropped node go with it rather than being rerouted into an invented shorter chain.
        var kept = projection.Nodes.Take(ceiling).ToList();
        var keptIds = kept.Select(x => (x.Kind, x.Id)).ToHashSet();
        return new(projectId, releaseId, kept,
            projection.Edges.Where(x => keptIds.Contains((x.FromKind, x.FromId))
                && keptIds.Contains((x.ToKind, x.ToId))).ToList(), true, ladder);
    }

    private const string NetworkRootKind = "BuildNetwork";

    /// <summary>
    /// Computes register state for a page in bounded set-based queries. The caller supplies only rows from one
    /// Project; the method still scopes every read to that Project so a cross-Project ID cannot leak state.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, ChangeRequestTraceState>> StatesAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> changeRequestIds,
        ILadderPolicy policy, CancellationToken ct)
    {
        var ids = changeRequestIds.Where(x => x != Guid.Empty).Distinct().ToHashSet();
        if (ids.Count == 0) return new Dictionary<Guid, ChangeRequestTraceState>();

        var rows = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => x.ProjectId == projectId && ids.Contains(x.Id))
            .Select(x => new CrRow(x.Id, x.ProjectId, x.TargetReleaseId, x.BaseNumber, x.Revision,
                x.Title, x.State, x.Type, x.SoftwareLevel, x.NoUpstreamRationale,
                x.InheritedUpstreamContextJson, x.UpstreamAnswerAffirmed))
            .ToListAsync(ct);
        return await ComputeStatesAsync(db, projectId, rows, policy, ct);
    }

    private static async Task<ChangeRequestTraceProjectionResult?> BuildAsync(
        AeroLinkDbContext db, Guid projectId, Guid rootId, string rootKind, ILadderPolicy policy,
        CancellationToken ct, Guid? networkReleaseId = null, TraceScope? selectedScope = null, TraceReadBudget? readBudget = null,
        bool directOnly = false)
    {
        var isNetwork = rootKind == NetworkRootKind;
        var budget = readBudget ?? new TraceReadBudget();
        var scope = selectedScope ?? await DiscoverAsync(db, projectId, rootId, rootKind, policy, budget, ct, directOnly);
        var crIds = scope.Changes.ToArray();
        var tcrIds = scope.Reviews.ToArray();
        var reqIds = scope.Requirements.ToArray();
        var codeIds = scope.Code.ToArray();
        var reportIds = scope.Reports.ToArray();
        var allCr = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => x.ProjectId == projectId && crIds.Contains(x.Id))
            .Select(x => new CrRow(x.Id, x.ProjectId, x.TargetReleaseId, x.BaseNumber, x.Revision,
                x.Title, x.State, x.Type, x.SoftwareLevel, x.NoUpstreamRationale,
                x.InheritedUpstreamContextJson, x.UpstreamAnswerAffirmed))
            .ReadTraceAsync(budget, ct);
        var byCr = allCr.ToDictionary(x => x.Id);
        var rootCr = rootKind == "ChangeRequest" && byCr.ContainsKey(rootId) ? rootId : Guid.Empty;
        if (rootKind == "ChangeRequest" && rootCr == Guid.Empty) return null;

        var authored = await (from link in db.ChangeRequestUpstreamLinks.AsNoTracking()
                              join child in db.SystemChangeRequests.AsNoTracking()
                                  on link.ChangeRequestId equals child.Id
                              join parent in db.SystemChangeRequests.AsNoTracking()
                                  on link.UpstreamChangeRequestId equals parent.Id
                              where child.ProjectId == projectId && parent.ProjectId == projectId
                                  && crIds.Contains(child.Id) && crIds.Contains(parent.Id)
                              select new { link.Id, link.ChangeRequestId, link.UpstreamChangeRequestId, link.UpstreamBuildId,
                                  link.UpstreamBuildVersion, link.Rationale, link.ActorId, link.StatedAt })
            .ReadTraceAsync(budget, ct);
        var assessments = await (from assessment in db.DownstreamChangeAssessments.AsNoTracking()
                                 join link in db.DownstreamAssessmentChangeRequestLinks.AsNoTracking()
                                     on assessment.Id equals link.AssessmentId
                                 where assessment.ProjectId == projectId
                                     && crIds.Contains(assessment.SourceChangeRequestId) && crIds.Contains(link.ChangeRequestId)
                                     && assessment.State != DownstreamAssessmentState.Superseded
                                 select new
                                 {
                                     assessment.Id, assessment.ProjectId, assessment.ReleaseId, assessment.State,
                                     assessment.Outcome, assessment.TargetLevel,
                                     assessment.SourceChangeRequestId, LinkId = link.Id,
                                     ChildId = link.ChangeRequestId
                                 }).ReadTraceAsync(budget, ct);
        authored = authored.Where(x => byCr.ContainsKey(x.ChangeRequestId)
            && byCr.ContainsKey(x.UpstreamChangeRequestId)).ToList();
        assessments = assessments.Where(x => byCr.TryGetValue(x.SourceChangeRequestId, out var source)
            && byCr.TryGetValue(x.ChildId, out var child)
            && IsCurrentAssessmentEdge(x.ProjectId, x.State, x.ReleaseId, x.TargetLevel,
                Identity(source), Identity(child), policy)).ToList();
        var snapshotIds = await BoundedSnapshotIdsAsync(db, projectId, crIds, budget, ct);
        var frozenCycles = await (from cycle in db.ReviewCycles.AsNoTracking()
                                  join change in db.SystemChangeRequests.AsNoTracking() on cycle.ChangeRequestId equals change.Id
                                  where change.ProjectId == projectId && snapshotIds.Contains(cycle.Id)
                                  select new { ChangeRequestId = cycle.ChangeRequestId!.Value,
                                      cycle.StartedAt, cycle.SnapshotContractVersion, cycle.SnapshotJson })
            .ReadTraceAsync(budget, ct);
        var frozenEvidence = frozenCycles.SelectMany(x => ParseFrozenTrace(x.SnapshotJson)).ToList();
        var frozenAssessmentIds = frozenEvidence.Where(x => x.AssessmentId != null).Select(x => x.AssessmentId!.Value).Distinct().ToArray();
        var liveFrozenAssessmentIds = new HashSet<Guid>();
        if (frozenAssessmentIds.Length > 0)
        {
            // A capped network may omit the current child of a frozen assessment. Its historical status still
            // answers whether that exact assessment has a valid live link anywhere in the authorized Project.
            var liveCandidates = await (from assessment in db.DownstreamChangeAssessments.AsNoTracking()
                join link in db.DownstreamAssessmentChangeRequestLinks.AsNoTracking() on assessment.Id equals link.AssessmentId
                join source in db.SystemChangeRequests.AsNoTracking() on assessment.SourceChangeRequestId equals source.Id
                join child in db.SystemChangeRequests.AsNoTracking() on link.ChangeRequestId equals child.Id
                where assessment.ProjectId == projectId && source.ProjectId == projectId && child.ProjectId == projectId
                    && frozenAssessmentIds.Contains(assessment.Id) && assessment.State != DownstreamAssessmentState.Superseded
                select new { assessment.Id, assessment.State, assessment.ReleaseId, assessment.TargetLevel,
                    Source = new CrIdentity(source.ProjectId, source.TargetReleaseId, source.Type, source.SoftwareLevel, source.State),
                    Child = new CrIdentity(child.ProjectId, child.TargetReleaseId, child.Type, child.SoftwareLevel, child.State) })
                .ReadTraceAsync(budget, ct);
            liveFrozenAssessmentIds.UnionWith(liveCandidates.Where(x => IsCurrentAssessmentEdge(projectId,
                x.State, x.ReleaseId, x.TargetLevel, x.Source, x.Child, policy)).Select(x => x.Id));
        }
        var evidenceBuildIds = frozenEvidence.SelectMany(x => new[] { x.BuildId, x.UpstreamBuildId })
            .Where(x => x != null).Select(x => x!.Value).Concat(authored.Select(x => x.UpstreamBuildId))
            .Concat(assessments.Select(x => x.ReleaseId)).Distinct().ToArray();
        var releaseRows = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId
                && (evidenceBuildIds.Contains(x.Id)
                    || db.SystemChangeRequests.Any(cr => cr.ProjectId == projectId && crIds.Contains(cr.Id) && cr.TargetReleaseId == x.Id)
                    || db.TestChangeReviews.Any(tcr => tcr.ProjectId == projectId && tcrIds.Contains(tcr.Id) && tcr.ReleaseId == x.Id)
                    || db.CodeTraceabilityRecords.Any(code => code.ProjectId == projectId && codeIds.Contains(code.Id) && code.ReleaseId == x.Id)
                    || db.ProblemReports.Any(report => report.ProjectId == projectId && reportIds.Contains(report.Id) && report.TargetReleaseId == x.Id)))
            .Select(x => new { x.Id, x.Version }).ReadTraceAsync(budget, ct);
        var releases = releaseRows.ToDictionary(x => x.Id, x => x.Version);
        var reopenings = await (from reopening in db.DownstreamAssessmentReopenings.AsNoTracking()
                                join assessment in db.DownstreamChangeAssessments.AsNoTracking() on reopening.AssessmentId equals assessment.Id
                                where assessment.ProjectId == projectId && frozenAssessmentIds.Contains(assessment.Id)
                                select new { reopening.Id, reopening.AssessmentId, reopening.Reason, reopening.ActorId,
                                    reopening.OccurredAt, reopening.PreviousState, reopening.PreviousOutcome }).ReadTraceAsync(budget, ct);
        var reopeningByAssessment = reopenings.GroupBy(x => x.AssessmentId)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.OccurredAt).ThenBy(y => y.Id).ToList());

        // A relation row is the traversal authority. Walk both directions over the exact CR identities and
        // retain one deterministic edge per pair; assessment and authored facts are merged, never duplicated.
        // The link is stored child → parent, but the story (#925 F5) reads upstream → downstream, so the
        // emitted edge points from the upstream change to the change that names it. The stored "Upstream"
        // relation and its provenance are unchanged, and the client phrases it for that reading.
        var pairEdges = new Dictionary<PairKey, EdgeBuilder>();
        void AddPair(Guid child, Guid parent, ChangeRequestTraceProvenance provenance)
        {
            if (!byCr.ContainsKey(child) || !byCr.ContainsKey(parent) || child == parent) return;
            var key = new PairKey(child, parent);
            if (!pairEdges.TryGetValue(key, out var edge))
                pairEdges[key] = edge = new(parent, "ChangeRequest", child, "ChangeRequest", "Upstream");
            if (!edge.Provenance.Contains(provenance)) edge.Provenance.Add(provenance);
        }
        foreach (var link in authored)
        {
            var eligible = byCr.TryGetValue(link.UpstreamChangeRequestId, out var parent)
                && byCr.TryGetValue(link.ChangeRequestId, out var child)
                && IsCurrentAuthoredPair(Identity(child), Identity(parent), link.UpstreamBuildId, policy);
            AddPair(link.ChangeRequestId, link.UpstreamChangeRequestId,
                new("AuthorStated", link.Id, Rationale: link.Rationale, ActorId: link.ActorId,
                    StatedAt: link.StatedAt, UpstreamBuildId: link.UpstreamBuildId,
                    UpstreamBuildVersion: link.UpstreamBuildVersion, IsLive: eligible,
                    Status: eligible ? null : "Upstream revision is not eligible for this exact parent/build relationship; corrective authoring is required."));
        }
        foreach (var link in assessments)
            AddPair(link.ChildId, link.SourceChangeRequestId,
                new("AssessmentDerived", link.Id, link.Id, link.LinkId,
                    BuildId: link.ReleaseId, BuildVersion: releases.GetValueOrDefault(link.ReleaseId)));
        // Reopened/corrected assessments remove their live link. A prior v3 review still needs to show the
        // exact evidence it froze, so retain it as historical provenance without reanimating a live edge.
        foreach (var cycle in frozenCycles.Where(x => byCr.ContainsKey(x.ChangeRequestId)
                     && x.SnapshotContractVersion >= 3))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var frozen in ParseFrozenTrace(cycle.SnapshotJson))
            {
                var reopening = frozen.AssessmentId is Guid assessmentId
                    && reopeningByAssessment.TryGetValue(assessmentId, out var recordedReopenings)
                    ? recordedReopenings.FirstOrDefault(x => x.OccurredAt >= cycle.StartedAt)
                    : null;
                var liveAssessment = frozen.AssessmentId is Guid liveAssessmentId
                    && liveFrozenAssessmentIds.Contains(liveAssessmentId);
                AddPair(cycle.ChangeRequestId, frozen.UpstreamId,
                    new(frozen.Kind, frozen.SourceId,
                        frozen.AssessmentId, frozen.AssessmentLinkId, IsLive: false,
                        Status: frozen.Kind == "FrozenReviewEvidence"
                            ? reopening is not null
                                ? "Frozen review evidence; assessment was reopened/corrected."
                                : liveAssessment
                                    ? "Frozen review evidence; live assessment remains present."
                                    : "Frozen review evidence retained; no reopening record is present."
                            : authored.Any(x => x.Id == frozen.SourceId)
                                ? "Frozen author-stated evidence; live authored link remains present."
                                : "Frozen author-stated evidence; live authored link was removed or replaced.",
                        Rationale: frozen.Rationale, ActorId: frozen.ActorId, StatedAt: frozen.StatedAt,
                        UpstreamBuildId: frozen.UpstreamBuildId, UpstreamBuildVersion: frozen.UpstreamBuildVersion,
                        BuildId: frozen.BuildId,
                        BuildVersion: frozen.BuildId is Guid buildId ? releases.GetValueOrDefault(buildId) : null,
                        ReopeningId: reopening?.Id, ReopeningReason: reopening?.Reason,
                        ReopenedBy: reopening?.ActorId, ReopenedAt: reopening?.OccurredAt,
                        PreviousState: reopening?.PreviousState.ToString(),
                        PreviousOutcome: reopening?.PreviousOutcome.ToString()));
            }
        }

        var nodes = new Dictionary<(string Kind, Guid Id), ChangeRequestTraceNode>();
        foreach (var id in byCr.Keys.OrderBy(x => x))
        {
            var row = byCr[id];
            nodes[("ChangeRequest", row.Id)] = new(row.Id, "ChangeRequest", Display(row.BaseNumber, row.Revision),
                row.Title, row.State.ToString(), row.ProjectId, row.TargetReleaseId,
                releases.GetValueOrDefault(row.TargetReleaseId), row.Revision, ChangeRequestLevel(row)?.ToString());
        }

        var edgeBuilders = pairEdges.Values.ToList();

        // Existing TCR source identities, including immutable package source snapshots, are all read in one
        // batch. Source snapshots are parsed only after the query; JSON is evidence, never a new relationship store.
        var reviewRows = await db.TestChangeReviews.AsNoTracking().Where(x => x.ProjectId == projectId && tcrIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ReleaseId, x.BaseNumber, x.Revision, x.Title, x.State, x.ArtifactKind,
                x.ChangeRequestId, x.OriginKind, x.OriginReferenceId })
            .ReadTraceAsync(budget, ct);
        var reviewIds = reviewRows.Select(x => x.Id).ToHashSet();
        var claims = await db.TestChangeRequestClaims.AsNoTracking()
            .Where(x => reviewIds.Contains(x.TestChangeReviewId))
            .Select(x => new { x.TestChangeReviewId, x.ChangeRequestId, x.Id })
            .ReadTraceAsync(budget, ct);
        var tcrById = reviewRows.ToDictionary(x => x.Id);
        if (rootKind == "TestChangeRequest" && !tcrById.ContainsKey(rootId)) return null;
        if (isNetwork)
            foreach (var review in reviewRows)
                nodes[("TestChangeRequest", review.Id)] = new(review.Id, "TestChangeRequest",
                    Display(review.BaseNumber, review.Revision), review.Title, review.State.ToString(), projectId,
                    review.ReleaseId, releases.GetValueOrDefault(review.ReleaseId), review.Revision, review.ArtifactKind.ToString());
        if (rootKind == "TestChangeRequest")
        {
            var rootTcr = tcrById[rootId];
            nodes[("TestChangeRequest", rootTcr.Id)] = new(rootTcr.Id, "TestChangeRequest",
                Display(rootTcr.BaseNumber, rootTcr.Revision), rootTcr.Title, rootTcr.State.ToString(), projectId,
                rootTcr.ReleaseId, releases.GetValueOrDefault(rootTcr.ReleaseId), rootTcr.Revision,
                rootTcr.ArtifactKind.ToString());
        }
        var tcrEdgeKeys = new HashSet<(Guid, Guid, string)>();
        void AddTcrSource(Guid crId, Guid tcrId, string kind, Guid? sourceId = null, Guid? procedureRevisionId = null)
        {
            if (!byCr.ContainsKey(crId) || !tcrById.ContainsKey(tcrId)) return;
            var key = (crId, tcrId, kind);
            if (!tcrEdgeKeys.Add(key)) return;
            if (!nodes.ContainsKey(("TestChangeRequest", tcrId)))
            {
                var tcr = tcrById[tcrId];
                nodes[("TestChangeRequest", tcr.Id)] = new(tcr.Id, "TestChangeRequest",
                    Display(tcr.BaseNumber, tcr.Revision), tcr.Title, tcr.State.ToString(), projectId,
                    tcr.ReleaseId, releases.GetValueOrDefault(tcr.ReleaseId), tcr.Revision, tcr.ArtifactKind.ToString());
            }
            var edge = new EdgeBuilder(crId, "ChangeRequest", tcrId, "TestChangeRequest", "CoveredByTestChangeRequest");
            edge.Provenance.Add(new(kind, sourceId ?? tcrId, ProcedureRevisionId: procedureRevisionId));
            edgeBuilders.Add(edge);
        }
        foreach (var review in reviewRows.Where(x => x.OriginKind == TestChangeReviewOriginKind.ChangeRequest
                     && x.ChangeRequestId is not null))
            AddTcrSource(review.ChangeRequestId!.Value, review.Id, "TcrOrigin");
        foreach (var claim in claims)
            AddTcrSource(claim.ChangeRequestId, claim.TestChangeReviewId, "TcrAdditionalSource", claim.Id);

        // A software Procedure TCR names its exact Case origin through an existing discriminator. Resolve each
        // discriminator to its owning Case TCR, preserving the source identity and not inventing a CR link.
        var procedureTcrs = reviewRows.Where(x => x.ArtifactKind == VerificationArtifactKind.Procedure
                && CaseOrigins.Contains(x.OriginKind)).ToList();
        var changeOrigins = await db.Set<TestProcedureChange>().AsNoTracking()
            .Where(x => procedureTcrs.Select(p => p.OriginReferenceId).Contains(x.Id))
            .Select(x => new { x.Id, x.TestChangeReviewId }).ReadTraceAsync(budget, ct);
        var assessmentOrigins = await db.VerificationImpactItems.AsNoTracking()
            .Where(x => procedureTcrs.Select(p => p.OriginReferenceId).Contains(x.Id))
            .Select(x => new { x.Id, x.TestChangeReviewId }).ReadTraceAsync(budget, ct);
        var caseProcedurePairs = procedureTcrs.Select(procedureTcr =>
        {
            var caseTcr = procedureTcr.OriginKind switch
            {
                TestChangeReviewOriginKind.CaseChange => changeOrigins.FirstOrDefault(x => x.Id == procedureTcr.OriginReferenceId)?.TestChangeReviewId,
                TestChangeReviewOriginKind.CaseAssessment => assessmentOrigins.FirstOrDefault(x => x.Id == procedureTcr.OriginReferenceId)?.TestChangeReviewId,
                TestChangeReviewOriginKind.CaseReview => procedureTcr.OriginReferenceId,
                _ => null,
            };
            return (procedureTcr, caseTcr);
        }).Where(x => x.caseTcr is not null).ToList();
        // Case ancestry is an undirected typed relation. Materialize every valid discriminator pair first;
        // the single component walk below decides which pairs are reachable from the requested CR.
        foreach (var pair in caseProcedurePairs)
        {
            var procedureTcr = pair.procedureTcr;
            var caseTcr = pair.caseTcr!.Value;
            if (!tcrById.ContainsKey(caseTcr)) continue;
            if (!nodes.ContainsKey(("TestChangeRequest", caseTcr)) && tcrById.TryGetValue(caseTcr, out var caseReview))
                nodes[("TestChangeRequest", caseTcr)] = new(caseReview.Id, "TestChangeRequest",
                    Display(caseReview.BaseNumber, caseReview.Revision), caseReview.Title,
                    caseReview.State.ToString(), projectId, caseReview.ReleaseId,
                    releases.GetValueOrDefault(caseReview.ReleaseId), caseReview.Revision,
                    caseReview.ArtifactKind.ToString());
            if (!nodes.ContainsKey(("TestChangeRequest", procedureTcr.Id)))
                nodes[("TestChangeRequest", procedureTcr.Id)] = new(procedureTcr.Id, "TestChangeRequest",
                    Display(procedureTcr.BaseNumber, procedureTcr.Revision), procedureTcr.Title,
                    procedureTcr.State.ToString(), projectId, procedureTcr.ReleaseId,
                    releases.GetValueOrDefault(procedureTcr.ReleaseId), procedureTcr.Revision,
                    procedureTcr.ArtifactKind.ToString());
            var edge = new EdgeBuilder(caseTcr, "TestChangeRequest", procedureTcr.Id, "TestChangeRequest",
                "CaseToProcedureOrigin");
            edge.Provenance.Add(new($"Case{procedureTcr.OriginKind.ToString()[4..]}Origin", procedureTcr.OriginReferenceId));
            edgeBuilders.Add(edge);
        }

        // Exact requirement and code sources are a separate typed slice of the same projection.
        var requirementRevisions = await (from revision in db.RequirementRevisions.AsNoTracking()
                                           join artifact in db.Requirements.AsNoTracking()
                                               on revision.ArtifactId equals artifact.Id
                                           where artifact.ProjectId == projectId && reqIds.Contains(revision.Id)
                                           select new { revision.Id, revision.ArtifactId, artifact.BaseNumber,
                                               revision.Revision, revision.Statement, artifact.Level,
                                               revision.SourceChangeRequestId }).ReadTraceAsync(budget, ct);
        var requirementRevisionIds = requirementRevisions.Select(x => x.Id).ToList();
        var baselineMemberships = await (from selection in db.BaselineRequirements.AsNoTracking()
                                         join baseline in db.CandidateBaselines.AsNoTracking()
                                             on selection.BaselineId equals baseline.Id
                                         where baseline.ProjectId == projectId
                                             && requirementRevisionIds.Contains(selection.RevisionId)
                                         select new { selection.RevisionId, selection.BaselineId }).ReadTraceAsync(budget, ct);
        var membershipsByRevision = baselineMemberships
            .GroupBy(x => x.RevisionId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<Guid>)x.Select(y => y.BaselineId).Distinct().OrderBy(y => y).ToList());
        // Suspect links are excluded from both reads, and the reason is worth stating because it looks like an
        // omission.
        //
        // A suspect lifecycle exists only for ExactLinkKind.RequirementTrace and ExactLinkKind.CaseProcedure.
        // Neither is a relation the change network draws: that board renders ProblemReportResolution, change to
        // upstream change, and CoveredByTestChangeRequest, between ChangeRequest, TestChangeRequest and
        // ProblemReport nodes only. So widening this set for the network would change nothing there — the
        // RequirementRevision endpoints are not in its visited set and the edges are dropped anyway.
        //
        // Requirement-trace suspectness becomes visible when a view actually shows requirement revisions, which
        // is the artifact thread of §5.3 in slice 5. That slice owns the decision, because showing suspect links
        // in the rooted trace would also change what the register inspector shows, and #866 decision 4 fixed
        // that deliberately.
        var liveOnly = true;
        var allRequirementLinks = await db.RequirementTraces.AsNoTracking()
            .Where(x => x.ProjectId == projectId && reqIds.Contains(x.SourceRevisionId) && reqIds.Contains(x.TargetRevisionId))
            .Select(x => new { x.Id, x.SourceRevisionId, x.TargetRevisionId, x.Type,
                x.ExactLinkSuspectLifecycleId,
                IsSuspect = x.ExactLinkSuspectLifecycleId != null
                    && db.ExactLinkSuspectLifecycles.Any(lifecycle =>
                        lifecycle.Id == x.ExactLinkSuspectLifecycleId
                        && lifecycle.ProjectId == projectId
                        && lifecycle.LinkKind == ExactLinkKind.RequirementTrace
                        && lifecycle.LinkId == x.Id
                        && lifecycle.State != ExactLinkLifecycleState.Closed) })
            .ReadTraceAsync(budget, ct);
        if (liveOnly) allRequirementLinks = allRequirementLinks.Where(x => !x.IsSuspect).ToList();
        foreach (var requirement in requirementRevisions)
        {
            nodes[("RequirementRevision", requirement.Id)] = new(requirement.Id, "RequirementRevision",
                Display(requirement.BaseNumber, requirement.Revision), requirement.Statement, null, projectId,
                null, null, requirement.Revision, requirement.Level.ToString(), requirement.ArtifactId,
                membershipsByRevision.GetValueOrDefault(requirement.Id));
            if (requirement.SourceChangeRequestId is Guid sourceId && byCr.ContainsKey(sourceId))
            {
                var edge = new EdgeBuilder(sourceId, "ChangeRequest", requirement.Id,
                    "RequirementRevision", "OwnsRequirementRevision");
                edge.Provenance.Add(new("RequirementRevisionSource", sourceId));
                edgeBuilders.Add(edge);
            }
        }
        foreach (var link in allRequirementLinks)
        {
            var edge = new EdgeBuilder(link.SourceRevisionId, "RequirementRevision", link.TargetRevisionId,
                "RequirementRevision", "RequirementTrace", link.IsSuspect);
            edge.Provenance.Add(new("RequirementTrace", link.Id));
            edgeBuilders.Add(edge);
        }
        var code = await db.CodeTraceabilityRecords.AsNoTracking()
            .Where(x => x.ProjectId == projectId && codeIds.Contains(x.Id))
            .Select(x => new { x.Id, x.RequirementRevisionId, x.ReleaseId, x.Disposition, x.MergeRequestReference })
            .ReadTraceAsync(budget, ct);
        foreach (var record in code)
        {
            nodes[("CodeTraceability", record.Id)] = new(record.Id, "CodeTraceability", record.MergeRequestReference,
                null, record.Disposition.ToString(), projectId, record.ReleaseId,
                releases.GetValueOrDefault(record.ReleaseId), null, null);
            var edge = new EdgeBuilder(record.RequirementRevisionId, "RequirementRevision", record.Id,
                "CodeTraceability", "RequirementCodeEvidence");
            edge.Provenance.Add(new("CodeTraceabilityRecord", record.Id));
            edgeBuilders.Add(edge);
        }

        // Problem reports enter only the build network. The rooted trace deliberately does not carry them —
        // #866 decision 4 fixed the register inspector at one hop and its output is depended upon — so this
        // block is scoped rather than added to the shared graph.
        if (isNetwork)
        {
            var reports = await db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == projectId && reportIds.Contains(x.Id))
                .Select(x => new { x.Id, x.ReportNumber, x.Revision, x.Title, x.State, x.TargetReleaseId }).ReadTraceAsync(budget, ct);
            foreach (var report in reports)
                nodes[("ProblemReport", report.Id)] = new(report.Id, "ProblemReport", Display(report.ReportNumber, report.Revision),
                    report.Title, report.State.ToString(), projectId, report.TargetReleaseId,
                    report.TargetReleaseId is Guid build ? releases.GetValueOrDefault(build) : null, report.Revision, null);
            var problemLinks = await (from link in db.ProblemReportLinks.AsNoTracking()
                                      join report in db.ProblemReports.AsNoTracking()
                                          on link.ProblemReportId equals report.Id
                                      where report.ProjectId == projectId && reportIds.Contains(report.Id)
                                          && ((link.ArtifactType == "ChangeRequest" && crIds.Contains(link.ArtifactId)) || (link.ArtifactType == "TestChangeRequest" && tcrIds.Contains(link.ArtifactId)))
                                          && (link.ArtifactType == "ChangeRequest"
                                              || link.ArtifactType == "TestChangeRequest")
                                      select new { link.Id, link.ProblemReportId, link.ArtifactType,
                                          link.ArtifactId, link.Relationship, report.ReportNumber,
                                          report.Revision, report.Title, report.State, report.TargetReleaseId })
                .ReadTraceAsync(budget, ct);
            foreach (var link in problemLinks)
            {
                var targetKind = link.ArtifactType;
                if (targetKind == "ChangeRequest" && !byCr.ContainsKey(link.ArtifactId)) continue;
                if (targetKind == "TestChangeRequest" && !tcrById.ContainsKey(link.ArtifactId)) continue;
                nodes[("ProblemReport", link.ProblemReportId)] = new(link.ProblemReportId, "ProblemReport",
                    Display(link.ReportNumber, link.Revision), link.Title, link.State.ToString(), projectId,
                    link.TargetReleaseId,
                    link.TargetReleaseId is Guid prBuild ? releases.GetValueOrDefault(prBuild) : null,
                    link.Revision, null);
                var edge = new EdgeBuilder(link.ProblemReportId, "ProblemReport", link.ArtifactId, targetKind,
                    "ProblemReportResolution");
                edge.Provenance.Add(new("ProblemReportLink", link.Id, Status: link.Relationship));
                edgeBuilders.Add(edge);
            }
        }

        // All typed relations have now been materialized. One undirected visited-set walk is the only
        // component boundary: it prevents unrelated Project TCRs, requirements, and code records from
        // leaking while still reaching late-discovered CR/TCR/requirement chains in either direction.
        var graph = new Dictionary<(string Kind, Guid Id), HashSet<(string Kind, Guid Id)>>();
        void Connect((string Kind, Guid Id) from, (string Kind, Guid Id) to)
        {
            if (!graph.TryGetValue(from, out var fromSet)) graph[from] = fromSet = [];
            if (!graph.TryGetValue(to, out var toSet)) graph[to] = toSet = [];
            fromSet.Add(to); toSet.Add(from);
        }
        var typedEdges = edgeBuilders.Where(x => nodes.ContainsKey((x.FromKind, x.FromId))
                && nodes.ContainsKey((x.ToKind, x.ToId))
                && (!directOnly || (x.FromKind == rootKind && x.FromId == rootId)
                    || (x.ToKind == rootKind && x.ToId == rootId))).ToList();
        foreach (var edge in typedEdges)
        {
            ct.ThrowIfCancellationRequested();
            Connect((edge.FromKind, edge.FromId), (edge.ToKind, edge.ToId));
        }
        var visited = new HashSet<(string Kind, Guid Id)>();
        if (isNetwork)
        {
            // Build scope is membership, not reachability: an isolated change still belongs to its build and
            // must appear. Change and test-change requests are in by exact target build; a problem report is
            // in because it links to one of them, which is what "feeding this build" means here.
            foreach (var entry in nodes)
                if ((entry.Key.Kind == "ChangeRequest" || entry.Key.Kind == "TestChangeRequest")
                    && entry.Value.BuildId == networkReleaseId)
                    visited.Add(entry.Key);
            foreach (var reportId in scope.Reports)
                if (nodes.ContainsKey(("ProblemReport", reportId))) visited.Add(("ProblemReport", reportId));
        }
        else
        {
            var pending = new Stack<(string Kind, Guid Id)>([(rootKind, rootId)]);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = pending.Pop();
                if (!visited.Add(current) || !graph.TryGetValue(current, out var next)) continue;
                foreach (var node in next.OrderByDescending(x => x.Kind).ThenByDescending(x => x.Id)) pending.Push(node);
            }
        }
        // The rooted trace's register state is what the inspector reads. The network does not present it and
        // computing it per change request would be the expensive part of an otherwise single-pass read.
        var state = rootKind == "ChangeRequest"
            ? (await ComputeStatesAsync(db, projectId,
                [byCr[rootCr]], policy, ct, budget))[rootCr]
            : null;
        var edges = typedEdges.Where(x => visited.Contains((x.FromKind, x.FromId))
                && visited.Contains((x.ToKind, x.ToId)))
            .GroupBy(x => (x.FromId, x.FromKind, x.ToId, x.ToKind, x.Relation))
            .Select(group => new ChangeRequestTraceEdge(group.Key.FromId, group.Key.FromKind, group.Key.ToId,
                group.Key.ToKind, group.Key.Relation, group.SelectMany(x => x.Provenance)
                    .Distinct().OrderBy(x => x.Kind).ThenBy(x => x.SourceId).ToList(),
                    group.Any(x => x.IsSuspect)))
            .OrderBy(x => x.FromKind).ThenBy(x => x.FromId).ThenBy(x => x.ToKind).ThenBy(x => x.ToId)
            .ThenBy(x => x.Relation).ToList();
        ct.ThrowIfCancellationRequested();
        return new(projectId, rootKind == "ChangeRequest" ? rootCr : Guid.Empty,
            nodes.Where(x => visited.Contains(x.Key)).Select(x => x.Value)
                .OrderBy(x => x.Kind).ThenBy(x => x.DisplayNumber).ThenBy(x => x.Id).ToList(), edges, state,
            rootId, rootKind, directOnly);
    }

    private static async Task<IReadOnlyDictionary<Guid, ChangeRequestTraceState>> ComputeStatesAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<CrRow> rows, ILadderPolicy policy,
        CancellationToken ct, TraceReadBudget? budget = null)
    {
        var ids = rows.Select(x => x.Id).ToHashSet();
        var links = await (from link in db.ChangeRequestUpstreamLinks.AsNoTracking()
            join source in db.SystemChangeRequests.AsNoTracking() on link.UpstreamChangeRequestId equals source.Id
            where ids.Contains(link.ChangeRequestId)
            select new { link.ChangeRequestId, link.UpstreamChangeRequestId, link.UpstreamBuildId,
                Source = new CrIdentity(source.ProjectId, source.TargetReleaseId, source.Type, source.SoftwareLevel, source.State) })
            .ReadTraceAsync(budget, ct);
        // Read the assessment decision independently from its optional child links. An assessment can be
        // Pending, NoChangeRequired, or ChangeRequired before a downstream CR exists; an inner join would
        // erase that authoritative state and incorrectly report NoDownstreamWork.
        var assessments = await db.DownstreamChangeAssessments.AsNoTracking()
            .Where(x => x.ProjectId == projectId
                && (ids.Contains(x.SourceChangeRequestId)
                    || db.DownstreamAssessmentChangeRequestLinks.Any(link => link.AssessmentId == x.Id
                        && ids.Contains(link.ChangeRequestId))))
            .Select(x => new { x.Id, x.ProjectId, x.SourceChangeRequestId, x.State, x.Outcome,
                x.ReleaseId, x.TargetLevel })
            .ReadTraceAsync(budget, ct);
        var assessmentIds = assessments.Select(x => x.Id).ToHashSet();
        var assessmentLinks = await db.DownstreamAssessmentChangeRequestLinks.AsNoTracking()
            .Where(x => assessmentIds.Contains(x.AssessmentId))
            .Select(x => new { Id = x.AssessmentId, ChildId = x.ChangeRequestId })
            .ReadTraceAsync(budget, ct);
        var assessmentById = assessments.ToDictionary(x => x.Id);
        var targetIds = assessmentLinks.Select(x => x.ChildId).Distinct().ToList();
        var identityIds = assessments.Select(x => x.SourceChangeRequestId).Concat(targetIds).Distinct().ToList();
        var identities = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => x.ProjectId == projectId && identityIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ProjectId, x.TargetReleaseId, x.Type, x.SoftwareLevel,
                x.BaseNumber, x.Revision, x.State }).ReadTraceAsync(budget, ct);
        var targets = identities.Where(x => targetIds.Contains(x.Id)).ToList();
        var targetById = targets.ToDictionary(x => x.Id);
        var sourceIds = assessments.Select(x => x.SourceChangeRequestId).Distinct().ToList();
        var sourceById = identities.Where(x => sourceIds.Contains(x.Id)).ToDictionary(x => x.Id,
            x => new CrIdentity(x.ProjectId, x.TargetReleaseId, x.Type, x.SoftwareLevel, x.State));
        foreach (var row in rows)
            sourceById[row.Id] = Identity(row);
        var targetIdentityById = targets.ToDictionary(x => x.Id,
            x => new CrIdentity(x.ProjectId, x.TargetReleaseId, x.Type, x.SoftwareLevel, x.State));
        var targetBaseNumbers = targets.Select(x => x.BaseNumber).Distinct().ToList();
        var latestTargetRevisionRows = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => x.ProjectId == projectId && targetBaseNumbers.Contains(x.BaseNumber))
            .GroupBy(x => x.BaseNumber)
            .Select(x => new { BaseNumber = x.Key, Revision = x.Max(y => y.Revision) })
            .ReadTraceAsync(budget, ct);
        var latestTargetRevision = latestTargetRevisionRows.ToDictionary(x => x.BaseNumber, x => x.Revision);
        var snapshotIds = budget is null ? null : await BoundedSnapshotIdsAsync(db, projectId, ids, budget, ct);
        var frozenCycles = await (from cycle in db.ReviewCycles.AsNoTracking()
                                  join change in db.SystemChangeRequests.AsNoTracking()
                                      on cycle.ChangeRequestId equals change.Id
                                  where change.ProjectId == projectId
                                      && cycle.ChangeRequestId != null && ids.Contains(cycle.ChangeRequestId.Value)
                                      && cycle.SnapshotContractVersion >= 3
                                      && (snapshotIds == null || snapshotIds.Contains(cycle.Id))
                                  select new { ChangeRequestId = cycle.ChangeRequestId!.Value, cycle.SnapshotJson })
            .ReadTraceAsync(budget, ct);
        var frozenAnswers = frozenCycles.GroupBy(x => x.ChangeRequestId)
            .ToDictionary(x => x.Key, x => x.Any(y => ParseFrozenTrace(y.SnapshotJson).Count > 0
                || HasFrozenAnswer(y.SnapshotJson)));
        var result = new Dictionary<Guid, ChangeRequestTraceState>();
        foreach (var row in rows)
        {
            var level = ChangeRequestLevel(row);
            if (level is null || !policy.OrderedLevels.Contains(level.Value))
            {
                result[row.Id] = new("NotApplicable", "NotApplicable", "OffLadder", false,
                    ["Historical change request level is not configured in the current Project ladder."]);
                continue;
            }
            var top = level is not null && policy.ParentLevels(level.Value).Count == 0;
            var rowLinks = links.Where(x => x.ChangeRequestId == row.Id).ToList();
            var derivedAnswer = assessmentLinks.Any(x => x.ChildId == row.Id
                && assessmentById.TryGetValue(x.Id, out var assessment)
                && sourceById.TryGetValue(assessment.SourceChangeRequestId, out var source)
                && targetIdentityById.TryGetValue(x.ChildId, out var child)
                && IsCurrentAssessmentEdge(assessment.ProjectId, assessment.State, assessment.ReleaseId, assessment.TargetLevel,
                    source, child, policy));
            var invalidAuthoredAnswer = rowLinks.Any(x => !IsCurrentAuthoredPair(Identity(row), x.Source, x.UpstreamBuildId, policy));
            var invalidDerivedAnswer = assessmentLinks.Any(x => x.ChildId == row.Id
                && assessmentById.TryGetValue(x.Id, out var assessment)
                && sourceById.TryGetValue(assessment.SourceChangeRequestId, out var source)
                && !ChangeRequestUpstreamEligibility.IsApproved(source.State)
                && IsApplicableAssessmentPair(assessment.ProjectId, assessment.State, assessment.ReleaseId,
                    assessment.TargetLevel, source, Identity(row), policy));
            var authoredAnswer = rowLinks.Count > 0 && !invalidAuthoredAnswer || !string.IsNullOrWhiteSpace(row.NoUpstreamRationale)
                || row.InheritedUpstreamContextJson is not null && row.UpstreamAnswerAffirmed;
            var frozenAnswer = frozenAnswers.GetValueOrDefault(row.Id);
            var historicalFrozenAnswer = row.State != ChangeRequestState.Draft && frozenAnswer;
            var upstream = top ? "Root" : invalidAuthoredAnswer || invalidDerivedAnswer ? "UpstreamGap" : authoredAnswer || derivedAnswer || historicalFrozenAnswer ? "Answered"
                : row.State == ChangeRequestState.Draft ? "IncompleteAuthoring" : "UpstreamGap";
            var warnings = new List<string>();
            if (invalidAuthoredAnswer || invalidDerivedAnswer)
                warnings.Add("An upstream revision is not eligible for this exact parent/build relationship; correct the active answer. Frozen reviews remain historical evidence.");
            if (!top && !authoredAnswer && !derivedAnswer && !historicalFrozenAnswer)
                warnings.Add(row.State == ChangeRequestState.Draft
                    ? "No upstream answer is authored yet; complete it before review."
                    : "No upstream answer was recorded for this historical change request.");

            var downstream = "NoDownstreamWork";
            if (row.State != ChangeRequestState.Draft)
            {
                var current = assessments.Where(x => x.SourceChangeRequestId == row.Id
                    && x.State != DownstreamAssessmentState.Superseded).ToList();
                if (current.Count > 0)
                {
                    var downstreamStates = current.Select(assessment =>
                    {
                        var targetsForAssessment = assessmentLinks.Where(x => x.Id == assessment.Id)
                            .Where(x => targetIdentityById.TryGetValue(x.ChildId, out var target)
                                && sourceById.TryGetValue(assessment.SourceChangeRequestId, out var source)
                                && IsCurrentAssessmentEdge(assessment.ProjectId, assessment.State, assessment.ReleaseId,
                                    assessment.TargetLevel, source, target, policy))
                            .Select(x => targetById.GetValueOrDefault(x.ChildId)).Where(x => x is not null)
                            .Select(x => x!).ToList();
                        if (assessment.Outcome == DownstreamAssessmentOutcome.Pending) return (State: "Pending", Warning: (string?)"Downstream assessment is pending.");
                        if (assessment.Outcome == DownstreamAssessmentOutcome.ChangeRequired && targetsForAssessment.Count == 0)
                            return (State: "ActionGap", Warning: (string?)"Downstream change is required but no change request is linked.");
                        if (assessment.Outcome == DownstreamAssessmentOutcome.NoChangeRequired)
                            return assessment.State == DownstreamAssessmentState.Approved
                                ? (State: "Satisfied", Warning: (string?)null) : (State: "ApprovalPending", Warning: "Downstream no-change answer is awaiting approval.");
                        var viableTargets = targetsForAssessment.Where(x => x.State != ChangeRequestState.Withdrawn
                            && latestTargetRevision.GetValueOrDefault(x.BaseNumber) == x.Revision).ToList();
                        if (viableTargets.Count == 0)
                            return (State: "ActionGap", Warning: (string?)"Linked downstream change request is no longer viable.");
                        if (viableTargets.Any(x => x.State == ChangeRequestState.Deferred))
                            return (State: "Deferred", Warning: (string?)"Linked downstream change request is deferred.");
                        return (State: "Linked", Warning: (string?)null);
                    }).ToList();
                    downstream = downstreamStates.Any(x => x.State == "ActionGap") ? "ActionGap"
                        : downstreamStates.Any(x => x.State == "Pending") ? "Pending"
                        : downstreamStates.Any(x => x.State == "ApprovalPending") ? "ApprovalPending"
                        : downstreamStates.Any(x => x.State == "Deferred") ? "Deferred"
                        : downstreamStates.All(x => x.State == "Satisfied") ? "Satisfied" : "Linked";
                    warnings.AddRange(downstreamStates.Where(x => x.Warning is not null).Select(x => x.Warning!));
                }
            }
            var overall = upstream is "IncompleteAuthoring" or "UpstreamGap" || downstream is "ActionGap" or "Pending" or "ApprovalPending"
                ? "ActionRequired" : downstream == "Deferred" ? "Deferred" : top && downstream == "NoDownstreamWork" ? "Root" : "Traced";
            result[row.Id] = new(upstream, downstream, overall, top, warnings.Distinct().OrderBy(x => x).ToList());
        }
        return result;
    }

    private static RequirementLevel? ChangeRequestLevel(CrRow row)
    {
        if (row.Type == ChangeRequestType.System) return RequirementLevel.System;
        if (row.Type == ChangeRequestType.Interface) return RequirementLevel.Interface;
        if (row.SoftwareLevel is not null) return row.SoftwareLevel;
        return null;
    }

    private static CrIdentity Identity(CrRow row) =>
        new(row.ProjectId, row.TargetReleaseId, row.Type, row.SoftwareLevel, row.State);

    private static bool IsCurrentAuthoredPair(CrIdentity child, CrIdentity source, Guid capturedBuildId, ILadderPolicy policy)
    {
        var childLevel = ChangeRequestLevel(child.Type, child.SoftwareLevel);
        var parentLevel = ChangeRequestLevel(source.Type, source.SoftwareLevel);
        return ChangeRequestUpstreamEligibility.IsApproved(source.State) && child.ProjectId == source.ProjectId
            && capturedBuildId == source.TargetReleaseId && childLevel is { } level && parentLevel is { } parent
            && policy.OrderedLevels.Contains(level) && policy.ParentLevels(level).Contains(parent);
    }

    private static bool IsCurrentAssessmentEdge(Guid projectId, DownstreamAssessmentState assessmentState,
        Guid assessmentReleaseId,
        RequirementLevel assessmentTargetLevel, CrIdentity source, CrIdentity child, ILadderPolicy policy)
        => ChangeRequestUpstreamEligibility.IsApproved(source.State)
            && IsApplicableAssessmentPair(projectId, assessmentState, assessmentReleaseId, assessmentTargetLevel, source, child, policy);

    private static bool IsApplicableAssessmentPair(Guid projectId, DownstreamAssessmentState assessmentState,
        Guid assessmentReleaseId, RequirementLevel assessmentTargetLevel, CrIdentity source, CrIdentity child, ILadderPolicy policy)
    {
        if (assessmentState == DownstreamAssessmentState.Superseded
            || source.ProjectId != projectId || child.ProjectId != projectId)
            return false;
        var sourceLevel = ChangeRequestLevel(source.Type, source.SoftwareLevel);
        var childLevel = ChangeRequestLevel(child.Type, child.SoftwareLevel);
        return sourceLevel is not null && childLevel is not null
            && policy.OrderedLevels.Contains(childLevel.Value)
            && assessmentReleaseId == child.TargetReleaseId
            && source.TargetReleaseId == child.TargetReleaseId
            && assessmentTargetLevel == childLevel
            && policy.ParentLevels(childLevel.Value).Contains(sourceLevel.Value);
    }

    private static RequirementLevel? ChangeRequestLevel(ChangeRequestType type, RequirementLevel? softwareLevel) =>
        type == ChangeRequestType.System ? RequirementLevel.System
        : type == ChangeRequestType.Interface ? RequirementLevel.Interface
        : type == ChangeRequestType.Software ? softwareLevel : null;

    private static string Display(string baseNumber, int revision) =>
        string.IsNullOrWhiteSpace(baseNumber) ? $".{revision:D2}" : $"{baseNumber}.{revision:D2}";

    private static IReadOnlyList<Guid> ParseSourceIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray()
                .Where(x => x.TryGetProperty("changeRequestId", out _)
                    || x.TryGetProperty("ChangeRequestId", out _))
                .Select(x => x.TryGetProperty("changeRequestId", out var lower) ? lower : x.GetProperty("ChangeRequestId"))
                .Select(x => x.TryGetGuid(out var id) ? id : Guid.Empty)
                .Where(x => x != Guid.Empty).Distinct().OrderBy(x => x).ToList();
        }
        catch (JsonException) { return []; }
    }

    private static bool HasFrozenAnswer(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (document.RootElement.TryGetProperty("noUpstreamRationale", out var rationale)
                && rationale.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(rationale.GetString())) return true;
            return document.RootElement.TryGetProperty("isTopOfLadder", out var top)
                && top.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }


}
