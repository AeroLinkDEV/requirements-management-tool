using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>The five artifact kinds §4.4 allows to open the artifact thread.</summary>
public enum ArtifactThreadFocalKind
{
    Requirement,
    Case,
    Procedure,
    Execution,
    Build,
}

/// <summary>
/// The six lanes of the artifact thread, in prototype order.
///
/// <para>
/// Taken from the canonical <c>Main.dc.html</c> (<c>THREAD.lanes</c>), which is the design contract per #880 §1:
/// <c>['PROBLEM REPORT', 'CHANGE REQUEST', 'REQUIREMENT', 'TEST CASE', 'PROCEDURE', 'RESULT · BUILD']</c>.
/// A result and a build share the final lane, so the thread carries an edge whose endpoints sit in one lane.
/// </para>
/// </summary>
public static class ArtifactThreadLane
{
    public const int ProblemReport = 0;
    public const int ChangeRequest = 1;
    public const int Requirement = 2;
    public const int Case = 3;
    public const int Procedure = 4;
    public const int ResultAndBuild = 5;
}

/// <summary>
/// One immutable evidence file recorded beneath an execution.
///
/// <para>
/// Carried whole rather than folded into the execution's free-text <see cref="TestExecution.EvidenceReference"/>.
/// The hash is the reason the record exists: a certification reviewer following the thread needs the file
/// identity, not a sentence about it. The prototype gives evidence no lane of its own, so these travel on the
/// execution node.
/// </para>
/// </summary>
public sealed record ArtifactThreadEvidence(
    Guid Id,
    string FileName,
    string ContentType,
    long Size,
    string Sha256,
    string UploadedBy,
    DateTimeOffset UploadedAt);

/// <summary>One exact node in the thread. Identity is always an exact revision, execution or build.</summary>
public sealed record ArtifactThreadNode(
    Guid Id,
    string Kind,
    int Lane,
    string? DisplayNumber,
    string? Title,
    string? State,
    string? Level,
    bool IsFocal,
    Guid? ArtifactId = null,
    int? Revision = null,
    string? Outcome = null,
    string? ExecutedBy = null,
    DateTimeOffset? ExecutedAt = null,
    DateTimeOffset? RecordedAt = null,
    IReadOnlyList<ArtifactThreadEvidence>? Evidence = null);

/// <summary>
/// One recorded relationship between two thread nodes.
///
/// <para>
/// <see cref="Relation"/> carries the authoritative relationship type rather than one generic word: an
/// <c>AllocatedFrom</c> requirement link and a <c>DerivedFrom</c> one are different controlled claims and must
/// not arrive indistinguishable. <see cref="FromKind"/> and <see cref="ToKind"/> mirror
/// <c>ChangeRequestTraceEdge</c>, so an edge can be read without first resolving both of its endpoints.
/// </para>
/// <para>
/// <see cref="IsSuspect"/> is server-stated, per #880 §8.3. The artifact thread is the first view able to carry
/// a true value: slice 3 established that no change-network relation can be suspect.
/// </para>
/// </summary>
public sealed record ArtifactThreadEdge(
    Guid FromId,
    string FromKind,
    Guid ToId,
    string ToKind,
    string Relation,
    bool IsSuspect);

/// <summary>
/// Whether the thread's requirement levels have a verification discipline at all, and if not, why.
///
/// <para>
/// <see cref="RequirementLevel"/> has five members but <see cref="VerificationDiscipline"/> has three.
/// <c>ProjectLadderConfiguration</c> refuses to name a discipline for Customer or Interface. A thread rooted on
/// one of those levels is not broken and must not be refused — it simply has no Case, Procedure or Result part,
/// and says so here rather than leaving the reader to guess why the chain stops at Requirement.
/// </para>
/// </summary>
public sealed record ArtifactThreadVerification(bool IsApplicable, string? Reason);

/// <summary>The whole thread for one focal artifact, inside one exact configuration context.</summary>
public sealed record ArtifactThreadResult(
    Guid ProjectId,
    Guid BaselineId,
    Guid? BuildId,
    string FocalKind,
    Guid FocalId,
    IReadOnlyList<ArtifactThreadNode> Nodes,
    IReadOnlyList<ArtifactThreadEdge> Edges,
    ArtifactThreadVerification Verification);

/// <summary>
/// The exact-revision chain behind #880 §5.3, rooted on any of the five focal kinds of §4.4.
///
/// <para>
/// <b>Scoped, not project-wide.</b> §8.2 requires these views to be build-scoped. The read takes the governed
/// <c>baselineId</c> the page already holds, and optionally an exact <c>buildId</c>. Results are restricted to
/// builds of that baseline, and to one build when it is named. Without this, a procedure revision executed in
/// two builds returned both run histories merged together, because the request carried no fact able to choose
/// between them.
/// </para>
/// <para>
/// <b>Directed, not a connected component.</b> The web is grown as two direction-pure walks from the focal
/// artifact — every ancestor, and every descendant — following #880 §6.5. Reversing direction at an ancestor
/// and continuing into its other children would pull in siblings that are neither upstream nor downstream of
/// what the reader opened. From a System focal both HLR children are downstream and belong; from one HLR
/// focal the other HLR is a sibling and does not.
/// </para>
/// <para>
/// <b>Focal-first.</b> The focal node is resolved and placed before any relationship is read, so an artifact
/// with no relationships still renders as a normal card (§6.8) instead of vanishing from its own thread.
/// </para>
/// <para>
/// It does not reuse <c>GET /api/traceability/path</c>, which backs the compact assurance strip: that read is
/// rooted only by requirement revision, walks by repeatedly taking one <c>.First()</c> branch, and resolves the
/// build as the newest recorded one. It equally does not define a second notion of coverage, trace or
/// suspectness — those are read as they stand, per #880 §4 and decision 23 of #866.
/// </para>
/// </summary>
public static class ArtifactThreadProjection
{
    private const string KindRequirement = "Requirement";
    private const string KindCase = "Case";
    private const string KindProcedure = "Procedure";
    private const string KindExecution = "Execution";
    private const string KindBuild = "Build";
    private const string KindChangeRequest = "ChangeRequest";
    private const string KindProblemReport = "ProblemReport";

    /// <summary>
    /// A link is suspect when it carries a lifecycle that is not yet Closed.
    ///
    /// <para>
    /// The rule the rest of the repository already applies — <c>ChangeRequestTraceProjection</c>,
    /// <c>CaseProcedureSatisfaction</c> and <c>ReleaseReadinessService</c> all treat a non-Closed lifecycle as
    /// live. Acknowledged and ChangeRequired are still suspect: the reader has seen the problem, not resolved it.
    /// </para>
    /// </summary>
    private static bool SuspectFromLifecycle(
        Guid? lifecycleId, IReadOnlyDictionary<Guid, ExactLinkLifecycleState> states) =>
        lifecycleId is Guid id && states.TryGetValue(id, out var state) && state != ExactLinkLifecycleState.Closed;

    /// <summary>The authoritative word for a recorded requirement trace, never flattened to one generic term.</summary>
    private static string RelationFor(RequirementTraceType type) => type switch
    {
        RequirementTraceType.AllocatedFrom => "allocated from",
        RequirementTraceType.DerivedFrom => "derived from",
        _ => type.ToString(),
    };

    private sealed class Accumulator
    {
        public readonly Dictionary<Guid, ArtifactThreadNode> Nodes = [];
        public readonly List<ArtifactThreadEdge> Edges = [];

        /// <summary>
        /// Later placements refine an existing node rather than being dropped, and the focal flag survives.
        ///
        /// <para>
        /// The focal artifact is resolved first, before the passes that know about evidence. Letting the first
        /// placement win would leave a focal execution permanently empty of the very files the reader opened it
        /// to see, so a richer later placement replaces it while carrying the focal flag forward.
        /// </para>
        /// </summary>
        public void Place(ArtifactThreadNode node)
        {
            if (!Nodes.TryGetValue(node.Id, out var existing)) { Nodes[node.Id] = node; return; }
            Nodes[node.Id] = node with
            {
                IsFocal = existing.IsFocal || node.IsFocal,
                Evidence = node.Evidence is { Count: > 0 } ? node.Evidence : existing.Evidence,
            };
        }

        /// <summary>An edge is only carried when both of its endpoints are on the board.</summary>
        public void Link(ArtifactThreadEdge edge)
        {
            if (Nodes.ContainsKey(edge.FromId) && Nodes.ContainsKey(edge.ToId)) Edges.Add(edge);
        }
    }

    public static async Task<ArtifactThreadResult?> BuildAsync(
        AeroLinkDbContext db, Guid projectId, Guid baselineId, Guid? buildId,
        ArtifactThreadFocalKind focalKind, Guid focalId, CancellationToken ct)
    {
        var baselineOwned = await db.CandidateBaselines.AsNoTracking()
            .AnyAsync(x => x.Id == baselineId && x.ProjectId == projectId, ct);
        if (!baselineOwned) return null;

        // Builds of this baseline are the only ones any result in this thread may belong to; a named build
        // narrows that to exactly one. This is the fact that keeps two builds' run histories apart.
        var scoped = await db.SoftwareBuilds.AsNoTracking()
            .Where(x => x.BaselineId == baselineId && x.ProjectId == projectId)
            .Select(x => new { x.Id, x.BuildNumber, x.Description, x.State })
            .ToListAsync(ct);
        if (buildId is Guid named)
        {
            scoped = scoped.Where(x => x.Id == named).ToList();
            if (scoped.Count == 0) return null;
        }
        IReadOnlyCollection<Guid> buildIds = scoped.Select(x => x.Id).ToHashSet();
        IReadOnlyDictionary<Guid, (string BuildNumber, string Description, SoftwareBuildState State)> builds =
            scoped.ToDictionary(x => x.Id, x => (x.BuildNumber, x.Description, x.State));

        var acc = new Accumulator();

        // Resolved and placed before any relationship is read. An artifact with no relationships is still its
        // own thread (§6.8), and a response that omitted the record the reader opened would answer a different
        // question from the one asked.
        var focal = await FocalAsync(db, projectId, focalKind, focalId, buildIds, ct);
        if (focal is null) return null;
        acc.Place(focal);

        // An Execution or a Build names its own configuration. Left at every build under the baseline, a peer
        // run of the same procedure in a sibling build would join the response as though it belonged to the
        // thread the reader opened, so the focal's own build narrows the scope when none was requested.
        if (buildId is null && focalKind is ArtifactThreadFocalKind.Execution or ArtifactThreadFocalKind.Build)
        {
            var anchorBuild = focalKind == ArtifactThreadFocalKind.Build
                ? focalId
                : await db.TestExecutions.AsNoTracking().Where(x => x.Id == focalId)
                    .Select(x => x.SoftwareBuildId).SingleOrDefaultAsync(ct);
            if (anchorBuild is Guid anchored && buildIds.Contains(anchored))
            {
                buildIds = [anchored];
                builds = builds.Where(x => x.Key == anchored).ToDictionary(x => x.Key, x => x.Value);
            }
        }

        // The verification artifacts the focal record itself names, read before any requirement is involved.
        // Growing the thread only through requirement coverage turned "the chain cannot continue farther
        // upstream" into "there are no recorded relationships": an execution of a procedure that covers
        // nothing lost both its procedure and its build, though it records them directly.
        var anchors = await AnchorsAsync(db, projectId, focalKind, focalId, buildIds, ct);

        var seeds = await CoveredRequirementsAsync(db, projectId, anchors.Count == 0 ? [] : anchors, ct);
        if (focalKind == ArtifactThreadFocalKind.Requirement) seeds = [focalId];
        var requirementIds = await WalkAsync(db, projectId, seeds, focalKind, acc, ct);

        await AddChangeAndProblemAsync(db, projectId, requirementIds, acc, ct);
        var verification = await AddVerificationAsync(db, projectId, requirementIds, anchors, focalKind, focalId,
            buildIds, builds, acc, ct);

        return new ArtifactThreadResult(projectId, baselineId, buildId, focalKind.ToString(), focalId,
            [.. acc.Nodes.Values], acc.Edges, verification);
    }

    /// <summary>
    /// Resolves the exact focal artifact, or null when it does not exist in this Project and context.
    ///
    /// <para>
    /// Case and Procedure are validated against the authoritative <see cref="VerificationArtifactKind"/>, so a
    /// Procedure revision presented as a Case fails closed rather than being served under the wrong word.
    /// </para>
    /// </summary>
    private static async Task<ArtifactThreadNode?> FocalAsync(
        AeroLinkDbContext db, Guid projectId, ArtifactThreadFocalKind kind, Guid focalId,
        IReadOnlyCollection<Guid> buildIds, CancellationToken ct)
    {
        switch (kind)
        {
            case ArtifactThreadFocalKind.Requirement:
            {
                var row = await (from revision in db.RequirementRevisions.AsNoTracking()
                                 join artifact in db.Requirements.AsNoTracking()
                                     on revision.ArtifactId equals artifact.Id
                                 where revision.Id == focalId && artifact.ProjectId == projectId
                                 select new
                                 {
                                     revision.Id, revision.ArtifactId, revision.Revision, revision.Statement,
                                     revision.State, artifact.BaseNumber, artifact.Level,
                                 }).SingleOrDefaultAsync(ct);
                return row is null ? null : new ArtifactThreadNode(row.Id, KindRequirement,
                    ArtifactThreadLane.Requirement, $"{row.BaseNumber}.{row.Revision:D2}", row.Statement,
                    row.State.ToString(), row.Level.ToString(), IsFocal: true, row.ArtifactId, row.Revision);
            }

            case ArtifactThreadFocalKind.Case:
            case ArtifactThreadFocalKind.Procedure:
            {
                var expected = kind == ArtifactThreadFocalKind.Case
                    ? VerificationArtifactKind.Case
                    : VerificationArtifactKind.Procedure;
                var row = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                 join procedure in db.TestProcedures.AsNoTracking()
                                     on revision.ProcedureId equals procedure.Id
                                 where revision.Id == focalId && procedure.ProjectId == projectId
                                     && procedure.ArtifactKind == expected
                                 select new
                                 {
                                     revision.Id, revision.Revision, revision.State,
                                     procedure.BaseNumber, procedure.Level, ArtifactId = procedure.Id,
                                 }).SingleOrDefaultAsync(ct);
                if (row is null) return null;
                var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db, [row.Id], ct);
                var isCase = expected == VerificationArtifactKind.Case;
                return new ArtifactThreadNode(row.Id, isCase ? KindCase : KindProcedure,
                    isCase ? ArtifactThreadLane.Case : ArtifactThreadLane.Procedure,
                    $"{row.BaseNumber}.{row.Revision:D2}",
                    titles.TryGetValue(row.Id, out var title) ? title.Title : null,
                    row.State.ToString(), row.Level.ToString(), IsFocal: true, row.ArtifactId, row.Revision);
            }

            case ArtifactThreadFocalKind.Execution:
            {
                var row = await db.TestExecutions.AsNoTracking()
                    .Where(x => x.Id == focalId && x.ProjectId == projectId)
                    .Select(x => new
                    {
                        x.Id, x.Outcome, x.ExecutedBy, x.ExecutedAt, x.RecordedAt, x.SoftwareBuildId,
                    }).SingleOrDefaultAsync(ct);
                // A run recorded against another build is not this configuration's run.
                if (row is null || row.SoftwareBuildId is null || !buildIds.Contains(row.SoftwareBuildId.Value))
                    return null;
                return ExecutionNode(row.Id, row.Outcome.ToString(), row.ExecutedBy, row.ExecutedAt,
                    row.RecordedAt, [], isFocal: true);
            }

            case ArtifactThreadFocalKind.Build:
            {
                if (!buildIds.Contains(focalId)) return null;
                var row = await db.SoftwareBuilds.AsNoTracking()
                    .Where(x => x.Id == focalId && x.ProjectId == projectId)
                    .Select(x => new { x.Id, x.BuildNumber, x.Description, x.State }).SingleOrDefaultAsync(ct);
                return row is null ? null : new ArtifactThreadNode(row.Id, KindBuild,
                    ArtifactThreadLane.ResultAndBuild, row.BuildNumber, row.Description, row.State.ToString(),
                    Level: null, IsFocal: true);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Executions carry no controlled number in this domain — <see cref="TestExecution"/> has no base number,
    /// and the prototype's <c>EXE-004821</c> is mockup text. Naming one would invent an identifier the
    /// certification record does not have, so the card is identified by outcome, actor and timing.
    /// </summary>
    private static ArtifactThreadNode ExecutionNode(Guid id, string outcome, string executedBy,
        DateTimeOffset executedAt, DateTimeOffset recordedAt, IReadOnlyList<ArtifactThreadEvidence> evidence,
        bool isFocal) =>
        new(id, KindExecution, ArtifactThreadLane.ResultAndBuild, DisplayNumber: null, Title: executedBy,
            State: outcome, Level: null, IsFocal: isFocal, ArtifactId: null, Revision: null, Outcome: outcome,
            ExecutedBy: executedBy, ExecutedAt: executedAt, RecordedAt: recordedAt, Evidence: evidence);

    /// <summary>
    /// The verification revisions the focal record itself names, independent of any requirement coverage.
    ///
    /// <para>
    /// This is what keeps a partial chain distinct from an unconnected record. An execution records its
    /// procedure; a build records its executions; a procedure records its cases. Those are direct exact facts,
    /// and a missing requirement above them stops the chain — it does not delete them.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> AnchorsAsync(
        AeroLinkDbContext db, Guid projectId, ArtifactThreadFocalKind kind, Guid focalId,
        IReadOnlyCollection<Guid> buildIds, CancellationToken ct)
    {
        switch (kind)
        {
            case ArtifactThreadFocalKind.Requirement:
                return [];

            case ArtifactThreadFocalKind.Case:
            {
                var procedures = await db.TestCaseProcedureLinks.AsNoTracking()
                    .Where(x => x.CaseRevisionId == focalId)
                    .Select(x => x.ProcedureRevisionId).ToListAsync(ct);
                return [focalId, .. procedures];
            }

            case ArtifactThreadFocalKind.Procedure:
            {
                var cases = await db.TestCaseProcedureLinks.AsNoTracking()
                    .Where(x => x.ProcedureRevisionId == focalId)
                    .Select(x => x.CaseRevisionId).ToListAsync(ct);
                return [focalId, .. cases];
            }

            case ArtifactThreadFocalKind.Execution:
            {
                var procedureRevisionId = await db.TestExecutions.AsNoTracking()
                    .Where(x => x.Id == focalId)
                    .Select(x => (Guid?)x.ProcedureRevisionId).SingleOrDefaultAsync(ct);
                if (procedureRevisionId is not Guid revisionId) return [];
                var cases = await db.TestCaseProcedureLinks.AsNoTracking()
                    .Where(x => x.ProcedureRevisionId == revisionId)
                    .Select(x => x.CaseRevisionId).ToListAsync(ct);
                return [revisionId, .. cases];
            }

            case ArtifactThreadFocalKind.Build:
            {
                // Driven by what the build actually evidences, not by every requirement in its baseline.
                // Seeding all baseline members and expanding would turn a thread into a build browser, which
                // §8.4 rules out, and would imply a relationship to this build that no record states.
                var procedures = await db.TestExecutions.AsNoTracking()
                    .Where(x => x.SoftwareBuildId != null && buildIds.Contains(x.SoftwareBuildId.Value)
                        && x.ProjectId == projectId)
                    .Select(x => x.ProcedureRevisionId).Distinct().ToListAsync(ct);
                if (procedures.Count == 0) return [];
                var cases = await db.TestCaseProcedureLinks.AsNoTracking()
                    .Where(x => procedures.Contains(x.ProcedureRevisionId))
                    .Select(x => x.CaseRevisionId).ToListAsync(ct);
                return [.. procedures, .. cases];
            }

            default:
                return [];
        }
    }

    private static async Task<IReadOnlyList<Guid>> CoveredRequirementsAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> verificationRevisionIds,
        CancellationToken ct)
    {
        // A Procedure may be reached directly (System covers requirements) or through its Case (HLR and LLR).
        // Both are followed from recorded rows rather than assumed from the level.
        if (verificationRevisionIds.Count == 0) return [];
        var reach = verificationRevisionIds.ToHashSet();
        var parents = await db.TestCaseProcedureLinks.AsNoTracking()
            .Where(x => reach.Contains(x.ProcedureRevisionId))
            .Select(x => x.CaseRevisionId).ToListAsync(ct);
        foreach (var parent in parents) reach.Add(parent);

        return await (from coverage in db.TestCoverage.AsNoTracking()
                      join revision in db.RequirementRevisions.AsNoTracking()
                          on coverage.RequirementRevisionId equals revision.Id
                      join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                      where reach.Contains(coverage.ProcedureRevisionId) && artifact.ProjectId == projectId
                      select coverage.RequirementRevisionId).Distinct().ToListAsync(ct);
    }

    /// <summary>
    /// Two direction-pure walks from the seeds: every ancestor, and every descendant.
    ///
    /// <para>
    /// Source is the child and Target its parent, matching the rest of the repository. Ancestors follow
    /// Source → Target and descendants follow Target → Source, neither ever turning round. An undirected walk
    /// reaches a sibling through the shared parent, and a sibling is neither upstream nor downstream of the
    /// focal artifact.
    /// </para>
    /// <para>
    /// Only a requirement focal owns a downstream chain. A Case, Procedure, Execution or Build is reached from
    /// the requirement side, so walking down from the requirements it covers would report peer requirements it
    /// has no recorded relationship with.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyCollection<Guid>> WalkAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyList<Guid> seeds, ArtifactThreadFocalKind focalKind,
        Accumulator acc, CancellationToken ct)
    {
        var links = await db.RequirementTraces.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .Select(x => new
            {
                x.Id, x.SourceRevisionId, x.TargetRevisionId, x.Type, x.ExactLinkSuspectLifecycleId,
            }).ToListAsync(ct);

        var upward = links.ToLookup(x => x.SourceRevisionId);
        var downward = links.ToLookup(x => x.TargetRevisionId);

        var reachable = new HashSet<Guid>(seeds);
        var queue = new Queue<Guid>(seeds);
        while (queue.Count > 0)
            foreach (var link in upward[queue.Dequeue()])
                if (reachable.Add(link.TargetRevisionId)) queue.Enqueue(link.TargetRevisionId);

        if (focalKind == ArtifactThreadFocalKind.Requirement)
        {
            var down = new HashSet<Guid>(seeds);
            queue = new Queue<Guid>(seeds);
            while (queue.Count > 0)
                foreach (var link in downward[queue.Dequeue()])
                    if (down.Add(link.SourceRevisionId)) queue.Enqueue(link.SourceRevisionId);
            reachable.UnionWith(down);
        }

        if (reachable.Count == 0) return [];

        // Project-scoped at the seam (§8.6): a revision reached through a link is admitted only if its own
        // artifact belongs to this Project.
        var rows = await (from revision in db.RequirementRevisions.AsNoTracking()
                          join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                          where reachable.Contains(revision.Id) && artifact.ProjectId == projectId
                          select new
                          {
                              revision.Id, revision.ArtifactId, revision.Revision, revision.Statement,
                              revision.State, artifact.BaseNumber, artifact.Level,
                          }).ToListAsync(ct);

        foreach (var row in rows)
            acc.Place(new ArtifactThreadNode(row.Id, KindRequirement, ArtifactThreadLane.Requirement,
                $"{row.BaseNumber}.{row.Revision:D2}", row.Statement, row.State.ToString(),
                row.Level.ToString(), IsFocal: false, row.ArtifactId, row.Revision));

        var states = await LifecycleStatesAsync(db, projectId,
            links.Where(x => x.ExactLinkSuspectLifecycleId is not null)
                .Select(x => x.ExactLinkSuspectLifecycleId!.Value).ToList(), ct);

        foreach (var link in links)
            acc.Link(new ArtifactThreadEdge(link.SourceRevisionId, KindRequirement, link.TargetRevisionId,
                KindRequirement, RelationFor(link.Type),
                SuspectFromLifecycle(link.ExactLinkSuspectLifecycleId, states)));

        return rows.Select(x => x.Id).ToHashSet();
    }

    private static async Task<IReadOnlyDictionary<Guid, ExactLinkLifecycleState>> LifecycleStatesAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, ExactLinkLifecycleState>();
        return await db.ExactLinkSuspectLifecycles.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.ProjectId == projectId)
            .ToDictionaryAsync(x => x.Id, x => x.State, ct);
    }

    /// <summary>Lanes 1 and 0: the change request each revision was authored under, and its problem reports.</summary>
    private static async Task AddChangeAndProblemAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> requirementIds,
        Accumulator acc, CancellationToken ct)
    {
        if (requirementIds.Count == 0) return;

        var authored = await db.RequirementRevisions.AsNoTracking()
            .Where(x => requirementIds.Contains(x.Id) && x.SourceChangeRequestId != null)
            .Select(x => new { RevisionId = x.Id, ChangeRequestId = x.SourceChangeRequestId!.Value })
            .ToListAsync(ct);
        var changeRequestIds = authored.Select(x => x.ChangeRequestId).Distinct().ToList();
        if (changeRequestIds.Count == 0) return;

        var changeRequests = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => changeRequestIds.Contains(x.Id) && x.ProjectId == projectId)
            .Select(x => new { x.Id, x.BaseNumber, x.Revision, x.Title, x.State })
            .ToListAsync(ct);

        foreach (var change in changeRequests)
            acc.Place(new ArtifactThreadNode(change.Id, KindChangeRequest, ArtifactThreadLane.ChangeRequest,
                $"{change.BaseNumber}.{change.Revision:D2}", change.Title, change.State.ToString(),
                Level: null, IsFocal: false));

        foreach (var row in authored)
            acc.Link(new ArtifactThreadEdge(row.ChangeRequestId, KindChangeRequest, row.RevisionId,
                KindRequirement, "authored", false));

        var known = changeRequests.Select(x => x.Id).ToHashSet();
        var links = await (from link in db.ProblemReportLinks.AsNoTracking()
                           join report in db.ProblemReports.AsNoTracking() on link.ProblemReportId equals report.Id
                           where known.Contains(link.ArtifactId) && report.ProjectId == projectId
                           select new
                           {
                               link.ArtifactId,
                               report.Id,
                               // Composed below rather than selected: ProblemReport.DisplayNumber is a computed
                               // property and EF cannot translate it into SQL.
                               report.ReportNumber,
                               report.Revision,
                               report.Title,
                               report.State,
                               link.Relationship,
                           }).ToListAsync(ct);

        foreach (var report in links.GroupBy(x => x.Id).Select(x => x.First()))
            acc.Place(new ArtifactThreadNode(report.Id, KindProblemReport, ArtifactThreadLane.ProblemReport,
                $"{report.ReportNumber}.{report.Revision:D2}", report.Title, report.State.ToString(),
                Level: null, IsFocal: false));

        foreach (var link in links)
            acc.Link(new ArtifactThreadEdge(link.Id, KindProblemReport, link.ArtifactId, KindChangeRequest,
                link.Relationship, false));
    }

    /// <summary>Lanes 3, 4 and 5, plus the applicability statement when the levels have no discipline.</summary>
    private static async Task<ArtifactThreadVerification> AddVerificationAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> requirementIds,
        IReadOnlyCollection<Guid> anchors,
        ArtifactThreadFocalKind focalKind, Guid focalId, IReadOnlyCollection<Guid> buildIds,
        IReadOnlyDictionary<Guid, (string Number, string Description, SoftwareBuildState State)> builds,
        Accumulator acc, CancellationToken ct)
    {
        if (requirementIds.Count > 0)
        {
            var levels = await (from revision in db.RequirementRevisions.AsNoTracking()
                                join artifact in db.Requirements.AsNoTracking()
                                    on revision.ArtifactId equals artifact.Id
                                where requirementIds.Contains(revision.Id)
                                select artifact.Level).Distinct().ToListAsync(ct);

            // A level either has a verification discipline or it does not; the domain is the authority and is
            // not widened here. Customer and Interface have none, so their chain truthfully stops.
            var without = levels.Where(level => !HasVerificationDiscipline(level)).ToList();
            if (levels.Count > 0 && without.Count == levels.Count && anchors.Count == 0)
            {
                var named = string.Join(" and ", without.Select(x => x.ToString()).OrderBy(x => x));
                return new ArtifactThreadVerification(false,
                    $"The {named} level has no verification discipline, so this thread has no test case, procedure or result.");
            }
        }

        var coverage = requirementIds.Count == 0
            ? []
            : await db.TestCoverage.AsNoTracking()
                .Where(x => requirementIds.Contains(x.RequirementRevisionId))
                .Select(x => new { x.ProcedureRevisionId, x.RequirementRevisionId, x.IsSuspect })
                .ToListAsync(ct);

        var directIds = coverage.Select(x => x.ProcedureRevisionId).Distinct().ToList();

        // Case-to-Procedure links are read from the anchor set as well as from coverage. A procedure opened
        // directly records its case whether or not that case covers a requirement, and deriving these links
        // from coverage alone silently dropped the one exact relationship such a thread has.
        var linkScope = directIds.Concat(anchors).Distinct().ToList();
        var caseLinks = linkScope.Count == 0
            ? []
            : await db.TestCaseProcedureLinks.AsNoTracking()
                .Where(x => linkScope.Contains(x.CaseRevisionId) || linkScope.Contains(x.ProcedureRevisionId))
                .Select(x => new { x.CaseRevisionId, x.ProcedureRevisionId, x.ExactLinkSuspectLifecycleId })
                .ToListAsync(ct);

        // The focal record's own anchors are always present, whether or not coverage reached them.
        var allRevisionIds = directIds
            .Concat(caseLinks.Select(x => x.ProcedureRevisionId))
            .Concat(caseLinks.Select(x => x.CaseRevisionId))
            .Concat(anchors).Distinct().ToList();
        var kindByRevision = new Dictionary<Guid, string>();
        if (allRevisionIds.Count > 0)
        {
            var artifacts = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                   join procedure in db.TestProcedures.AsNoTracking()
                                       on revision.ProcedureId equals procedure.Id
                                   where allRevisionIds.Contains(revision.Id) && procedure.ProjectId == projectId
                                   select new
                                   {
                                       revision.Id, procedure.BaseNumber, revision.Revision, revision.State,
                                       procedure.Level, procedure.ArtifactKind, ArtifactId = procedure.Id,
                                   }).ToListAsync(ct);

            var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(
                db, artifacts.Select(x => x.Id).Distinct().ToList(), ct);

            foreach (var artifact in artifacts)
            {
                var isCase = artifact.ArtifactKind == VerificationArtifactKind.Case;
                kindByRevision[artifact.Id] = isCase ? KindCase : KindProcedure;
                acc.Place(new ArtifactThreadNode(artifact.Id, isCase ? KindCase : KindProcedure,
                    isCase ? ArtifactThreadLane.Case : ArtifactThreadLane.Procedure,
                    $"{artifact.BaseNumber}.{artifact.Revision:D2}",
                    titles.TryGetValue(artifact.Id, out var title) ? title.Title : null,
                    artifact.State.ToString(), artifact.Level.ToString(),
                    IsFocal: (focalKind == ArtifactThreadFocalKind.Case
                        || focalKind == ArtifactThreadFocalKind.Procedure) && artifact.Id == focalId,
                    artifact.ArtifactId, artifact.Revision));
            }
        }

        foreach (var row in coverage)
            acc.Link(new ArtifactThreadEdge(row.RequirementRevisionId, KindRequirement, row.ProcedureRevisionId,
                kindByRevision.TryGetValue(row.ProcedureRevisionId, out var covering) ? covering : KindProcedure,
                "verified by", row.IsSuspect));

        var caseStates = await LifecycleStatesAsync(db, projectId,
            caseLinks.Where(x => x.ExactLinkSuspectLifecycleId is not null)
                .Select(x => x.ExactLinkSuspectLifecycleId!.Value).ToList(), ct);

        foreach (var link in caseLinks)
            acc.Link(new ArtifactThreadEdge(link.CaseRevisionId, KindCase, link.ProcedureRevisionId,
                KindProcedure, "run by", SuspectFromLifecycle(link.ExactLinkSuspectLifecycleId, caseStates)));

        await AddResultsAsync(db, projectId, allRevisionIds, buildIds, builds, focalKind, focalId, acc, ct);
        return new ArtifactThreadVerification(true, null);
    }

    /// <summary>
    /// Lane 5, which holds both executions and builds.
    ///
    /// <para>
    /// Every run recorded <b>inside the requested configuration</b> is returned, not the latest. A failed run
    /// and the retest that followed it are both part of the certification record, and showing only the newest
    /// would report a clean history that did not happen. The configuration scope is what keeps that bounded:
    /// runs from another build are a different context, not extra detail about this one.
    /// </para>
    /// <para>
    /// The build edge is intra-lane, per the prototype's <c>['EXE-004821', 'FMS-1.5.0', 'evidence for']</c>.
    /// </para>
    /// </summary>
    private static async Task AddResultsAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> procedureRevisionIds,
        IReadOnlyCollection<Guid> buildIds,
        IReadOnlyDictionary<Guid, (string Number, string Description, SoftwareBuildState State)> builds,
        ArtifactThreadFocalKind focalKind, Guid focalId, Accumulator acc, CancellationToken ct)
    {
        if (procedureRevisionIds.Count == 0 || buildIds.Count == 0) return;

        var executions = await db.TestExecutions.AsNoTracking()
            .Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId) && x.ProjectId == projectId
                && x.SoftwareBuildId != null && buildIds.Contains(x.SoftwareBuildId.Value))
            .Select(x => new
            {
                x.Id, x.ProcedureRevisionId, x.Outcome, x.SoftwareBuildId, x.ExecutedBy, x.ExecutedAt,
                x.RecordedAt, x.RetestOfExecutionId,
            })
            .ToListAsync(ct);
        if (executions.Count == 0) return;

        // Ordered client-side: SQLite cannot ORDER BY a DateTimeOffset column, and the API tests run on SQLite.
        var ordered = executions.OrderByDescending(x => x.ExecutedAt).ThenByDescending(x => x.RecordedAt).ToList();
        var executionIds = ordered.Select(x => x.Id).ToList();

        var evidence = await (from link in db.TestExecutionEvidence.AsNoTracking()
                              join record in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals record.Id
                              where executionIds.Contains(link.TestExecutionId) && record.ProjectId == projectId
                              select new
                              {
                                  link.TestExecutionId, record.Id, record.OriginalFileName, record.ContentType,
                                  record.Size, record.Sha256, record.UploadedBy, record.UploadedAt,
                              }).ToListAsync(ct);

        var byExecution = evidence.GroupBy(x => x.TestExecutionId).ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<ArtifactThreadEvidence>)group
                .Select(x => new ArtifactThreadEvidence(x.Id, x.OriginalFileName, x.ContentType, x.Size,
                    x.Sha256, x.UploadedBy, x.UploadedAt))
                .OrderBy(x => x.FileName, StringComparer.Ordinal).ToList());

        foreach (var execution in ordered)
        {
            acc.Place(ExecutionNode(execution.Id, execution.Outcome.ToString(), execution.ExecutedBy,
                execution.ExecutedAt, execution.RecordedAt,
                byExecution.TryGetValue(execution.Id, out var files) ? files : [],
                isFocal: focalKind == ArtifactThreadFocalKind.Execution && execution.Id == focalId));
            acc.Link(new ArtifactThreadEdge(execution.ProcedureRevisionId, KindProcedure, execution.Id,
                KindExecution, "produced", false));
        }

        foreach (var buildId in ordered.Where(x => x.SoftwareBuildId is not null)
                     .Select(x => x.SoftwareBuildId!.Value).Distinct())
        {
            if (!builds.TryGetValue(buildId, out var build)) continue;
            acc.Place(new ArtifactThreadNode(buildId, KindBuild, ArtifactThreadLane.ResultAndBuild,
                build.Number, build.Description, build.State.ToString(), Level: null,
                IsFocal: focalKind == ArtifactThreadFocalKind.Build && buildId == focalId));
        }

        foreach (var execution in ordered)
            if (execution.SoftwareBuildId is Guid buildId)
                acc.Link(new ArtifactThreadEdge(execution.Id, KindExecution, buildId, KindBuild,
                    "evidence for", false));

        // A retest is a recorded relationship between two runs, not an ordering the reader should have to infer
        // from timestamps. Link() drops it when the earlier run is outside the resolved scope, so a retest of a
        // failure in another build is never asserted here.
        foreach (var execution in ordered)
            if (execution.RetestOfExecutionId is Guid retestOf)
                acc.Link(new ArtifactThreadEdge(execution.Id, KindExecution, retestOf, KindExecution,
                    "retest of", false));
    }

    /// <summary>
    /// Whether this requirement level has a verification discipline.
    ///
    /// <para>
    /// Mirrors <c>ProjectLadderConfiguration</c>, which throws for any other level. Asking the question without
    /// raising is what lets the thread state the absence instead of failing.
    /// </para>
    /// </summary>
    private static bool HasVerificationDiscipline(RequirementLevel level) => level switch
    {
        RequirementLevel.System or RequirementLevel.HighLevel or RequirementLevel.LowLevel => true,
        _ => false,
    };
}
