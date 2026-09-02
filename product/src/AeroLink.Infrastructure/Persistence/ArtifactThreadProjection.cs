using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
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
    IReadOnlyList<ArtifactThreadEvidence>? Evidence = null);

/// <summary>
/// One recorded relationship between two thread nodes.
///
/// <para>
/// <see cref="IsSuspect"/> is server-stated, per #880 §8.3. The artifact thread is the first view able to carry
/// a true value: slice 3 established that no change-network relation can be suspect. Three different mechanisms
/// feed it, and all three are read here rather than inferred by the browser.
/// </para>
/// </summary>
public sealed record ArtifactThreadEdge(
    Guid FromId,
    Guid ToId,
    string Relation,
    bool IsSuspect);

/// <summary>
/// Whether the focal requirement's level has a verification discipline at all, and if not, why.
///
/// <para>
/// <see cref="RequirementLevel"/> has five members but <see cref="VerificationDiscipline"/> has three.
/// <c>ProjectLadderConfiguration</c> refuses to name a discipline for Customer or Interface. A thread rooted on
/// one of those levels is not broken and must not be refused — it simply has no Case, Procedure or Result part,
/// and says so here rather than leaving the reader to guess why the chain stops at Requirement.
/// </para>
/// </summary>
public sealed record ArtifactThreadVerification(bool IsApplicable, string? Reason);

/// <summary>The whole thread for one focal artifact.</summary>
public sealed record ArtifactThreadResult(
    Guid ProjectId,
    string FocalKind,
    Guid FocalId,
    IReadOnlyList<ArtifactThreadNode> Nodes,
    IReadOnlyList<ArtifactThreadEdge> Edges,
    ArtifactThreadVerification Verification);

/// <summary>
/// The exact-revision chain behind #880 §5.3, rooted on any of the five focal kinds of §4.4.
///
/// <para>
/// This is a Digital-Thread-specific read over the existing authoritative tables. It deliberately does not
/// reuse <c>GET /api/traceability/path</c>, which backs today's one-line lifecycle strip: that read is rooted
/// only by requirement revision, walks by repeatedly taking one <c>.First()</c> branch under tie-breakers, and
/// resolves the build as the most recently recorded one for the baseline. All three are correct for a strip and
/// wrong for a lane canvas — a requirement covered by two cases must show both.
/// </para>
/// <para>
/// It equally does not define a second notion of coverage, trace or suspectness. Coverage rows, exact links and
/// the shared <see cref="ExactLinkSuspectLifecycle"/> are read as they stand, per #880 §4 and decision 23 of
/// #866.
/// </para>
/// </summary>
public static class ArtifactThreadProjection
{
    /// <summary>
    /// A link is suspect when it carries a lifecycle that is not yet Closed.
    ///
    /// <para>
    /// This is the rule the rest of the repository already applies — <c>ChangeRequestTraceProjection</c>,
    /// <c>CaseProcedureSatisfaction</c> and <c>ReleaseReadinessService</c> all treat a non-Closed lifecycle as
    /// live, and the requirements workspace filters on <c>State == Closed</c> to exclude it. Acknowledged and
    /// ChangeRequired are still suspect: the reader has seen the problem, not resolved it.
    /// </para>
    /// </summary>
    private static bool SuspectFromLifecycle(Guid? lifecycleId, IReadOnlyDictionary<Guid, ExactLinkLifecycleState> states) =>
        lifecycleId is Guid id && states.TryGetValue(id, out var state) && state != ExactLinkLifecycleState.Closed;

    public static async Task<ArtifactThreadResult?> BuildAsync(
        AeroLinkDbContext db, Guid projectId, ArtifactThreadFocalKind focalKind, Guid focalId, CancellationToken ct)
    {
        // Every set below is seeded from the focal artifact and grown by recorded relationships only. Nothing is
        // resolved by display number, and nothing collapses two revisions of one artifact into a single node.
        var spine = await SpineAsync(db, projectId, focalKind, focalId, ct);
        if (spine is null) return null;

        var requirementRevisionIds = spine.RequirementRevisionIds;

        // Requirement lane: every exact revision reachable from the focal one through recorded traces, in both
        // directions, keeping all branches. The strip read walks one parent and one child; this keeps siblings.
        var traceLinks = await db.RequirementTraces.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .Select(x => new { x.Id, x.SourceRevisionId, x.TargetRevisionId, x.ExactLinkSuspectLifecycleId })
            .ToListAsync(ct);

        var reachable = new HashSet<Guid>(requirementRevisionIds);
        var frontier = new Queue<Guid>(requirementRevisionIds);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var link in traceLinks)
            {
                if (link.SourceRevisionId == current && reachable.Add(link.TargetRevisionId))
                    frontier.Enqueue(link.TargetRevisionId);
                if (link.TargetRevisionId == current && reachable.Add(link.SourceRevisionId))
                    frontier.Enqueue(link.SourceRevisionId);
            }
        }

        // Project-scoped at the seam (§8.6): a revision reached through a link is only admitted if its artifact
        // belongs to this Project. Defence in depth — the link table is already Project-scoped above.
        var requirementRows = await (from revision in db.RequirementRevisions.AsNoTracking()
                                     join artifact in db.Requirements.AsNoTracking()
                                         on revision.ArtifactId equals artifact.Id
                                     where reachable.Contains(revision.Id) && artifact.ProjectId == projectId
                                     select new
                                     {
                                         revision.Id,
                                         revision.ArtifactId,
                                         revision.Revision,
                                         revision.Statement,
                                         revision.State,
                                         revision.SourceChangeRequestId,
                                         artifact.BaseNumber,
                                         artifact.Level,
                                     }).ToListAsync(ct);

        var admitted = requirementRows.Select(x => x.Id).ToHashSet();
        var nodes = new List<ArtifactThreadNode>();
        var edges = new List<ArtifactThreadEdge>();

        foreach (var row in requirementRows)
        {
            nodes.Add(new ArtifactThreadNode(
                row.Id, "Requirement", ArtifactThreadLane.Requirement,
                $"{row.BaseNumber}.{row.Revision:D2}", row.Statement, row.State.ToString(),
                row.Level.ToString(), IsFocal: focalKind == ArtifactThreadFocalKind.Requirement && row.Id == focalId,
                row.ArtifactId, row.Revision));
        }

        var lifecycleIds = traceLinks.Where(x => x.ExactLinkSuspectLifecycleId is not null)
            .Select(x => x.ExactLinkSuspectLifecycleId!.Value).ToList();
        var lifecycleStates = await LifecycleStatesAsync(db, projectId, lifecycleIds, ct);

        foreach (var link in traceLinks)
        {
            if (!admitted.Contains(link.SourceRevisionId) || !admitted.Contains(link.TargetRevisionId)) continue;
            // Source is the child and Target its parent, matching the rest of the repository.
            edges.Add(new ArtifactThreadEdge(link.SourceRevisionId, link.TargetRevisionId, "traces to",
                SuspectFromLifecycle(link.ExactLinkSuspectLifecycleId, lifecycleStates)));
        }

        await AddChangeAndProblemAsync(db, projectId, requirementRows
            .Where(x => x.SourceChangeRequestId is not null)
            .Select(x => (x.Id, x.SourceChangeRequestId!.Value)).ToList(), nodes, edges, ct);

        var verification = await AddVerificationAsync(db, projectId, admitted, focalKind, focalId, nodes, edges, ct);

        return new ArtifactThreadResult(projectId, focalKind.ToString(), focalId, nodes, edges, verification);
    }

    private sealed record Spine(IReadOnlyList<Guid> RequirementRevisionIds);

    /// <summary>
    /// Resolves any of the five focal kinds to the requirement revisions its thread hangs from.
    ///
    /// <para>
    /// Returns null when the focal artifact does not exist in this Project, so the endpoint can answer 404
    /// without disclosing whether it exists elsewhere.
    /// </para>
    /// </summary>
    private static async Task<Spine?> SpineAsync(
        AeroLinkDbContext db, Guid projectId, ArtifactThreadFocalKind kind, Guid focalId, CancellationToken ct)
    {
        switch (kind)
        {
            case ArtifactThreadFocalKind.Requirement:
            {
                var exists = await (from revision in db.RequirementRevisions.AsNoTracking()
                                    join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                    where revision.Id == focalId && artifact.ProjectId == projectId
                                    select revision.Id).AnyAsync(ct);
                return exists ? new Spine([focalId]) : null;
            }

            case ArtifactThreadFocalKind.Case:
            case ArtifactThreadFocalKind.Procedure:
            {
                var owned = await VerificationRevisionExistsAsync(db, projectId, focalId, ct);
                if (!owned) return null;
                return new Spine(await RequirementsCoveredByAsync(db, projectId, [focalId], ct));
            }

            case ArtifactThreadFocalKind.Execution:
            {
                var procedureRevisionId = await db.TestExecutions.AsNoTracking()
                    .Where(x => x.Id == focalId && x.ProjectId == projectId)
                    .Select(x => (Guid?)x.ProcedureRevisionId).SingleOrDefaultAsync(ct);
                if (procedureRevisionId is not Guid revisionId) return null;
                return new Spine(await RequirementsCoveredByAsync(db, projectId, [revisionId], ct));
            }

            case ArtifactThreadFocalKind.Build:
            {
                var baselineId = await db.SoftwareBuilds.AsNoTracking()
                    .Where(x => x.Id == focalId && x.ProjectId == projectId)
                    .Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);
                if (baselineId is not Guid baseline) return null;
                var members = await db.BaselineRequirements.AsNoTracking()
                    .Where(x => x.BaselineId == baseline).Select(x => x.RevisionId).ToListAsync(ct);
                return new Spine(members);
            }

            default:
                return null;
        }
    }

    private static async Task<bool> VerificationRevisionExistsAsync(
        AeroLinkDbContext db, Guid projectId, Guid revisionId, CancellationToken ct) =>
        await (from revision in db.TestProcedureRevisions.AsNoTracking()
               join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
               where revision.Id == revisionId && procedure.ProjectId == projectId
               select revision.Id).AnyAsync(ct);

    /// <summary>
    /// Walks upward from verification revisions to the requirement revisions they cover.
    ///
    /// <para>
    /// A Procedure may be reached either directly (System, whose procedures cover requirements) or through its
    /// Case (HLR and LLR). Both paths are followed from recorded rows rather than assumed from the level, so a
    /// project configured differently still resolves correctly.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> RequirementsCoveredByAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> verificationRevisionIds, CancellationToken ct)
    {
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
        AeroLinkDbContext db, Guid projectId, IReadOnlyList<(Guid RevisionId, Guid ChangeRequestId)> authored,
        List<ArtifactThreadNode> nodes, List<ArtifactThreadEdge> edges, CancellationToken ct)
    {
        var changeRequestIds = authored.Select(x => x.ChangeRequestId).Distinct().ToList();
        if (changeRequestIds.Count == 0) return;

        var changeRequests = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => changeRequestIds.Contains(x.Id) && x.ProjectId == projectId)
            .Select(x => new { x.Id, x.BaseNumber, x.Revision, x.Title, x.State })
            .ToListAsync(ct);

        var known = changeRequests.Select(x => x.Id).ToHashSet();
        foreach (var change in changeRequests)
        {
            nodes.Add(new ArtifactThreadNode(change.Id, "ChangeRequest", ArtifactThreadLane.ChangeRequest,
                $"{change.BaseNumber}.{change.Revision:D2}", change.Title, change.State.ToString(),
                Level: null, IsFocal: false));
        }

        foreach (var (revisionId, changeRequestId) in authored)
        {
            if (known.Contains(changeRequestId))
                edges.Add(new ArtifactThreadEdge(changeRequestId, revisionId, "authored", false));
        }

        var links = await (from link in db.ProblemReportLinks.AsNoTracking()
                           join report in db.ProblemReports.AsNoTracking() on link.ProblemReportId equals report.Id
                           where known.Contains(link.ArtifactId) && report.ProjectId == projectId
                           select new
                           {
                               link.ArtifactId,
                               report.Id,
                               // Composed here rather than selected: ProblemReport.DisplayNumber is a computed
                               // property, and EF cannot translate it into SQL.
                               report.ReportNumber,
                               report.Revision,
                               report.Title,
                               report.State,
                               link.Relationship,
                           }).ToListAsync(ct);

        foreach (var report in links.GroupBy(x => x.Id).Select(x => x.First()))
        {
            nodes.Add(new ArtifactThreadNode(report.Id, "ProblemReport", ArtifactThreadLane.ProblemReport,
                $"{report.ReportNumber}.{report.Revision:D2}", report.Title, report.State.ToString(),
                Level: null, IsFocal: false));
        }

        foreach (var link in links)
            edges.Add(new ArtifactThreadEdge(link.Id, link.ArtifactId, link.Relationship, false));
    }

    /// <summary>Lanes 3, 4 and 5, plus the applicability statement when the level has no discipline.</summary>
    private static async Task<ArtifactThreadVerification> AddVerificationAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> requirementRevisionIds,
        ArtifactThreadFocalKind focalKind, Guid focalId,
        List<ArtifactThreadNode> nodes, List<ArtifactThreadEdge> edges, CancellationToken ct)
    {
        var levels = await (from revision in db.RequirementRevisions.AsNoTracking()
                            join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                            where requirementRevisionIds.Contains(revision.Id)
                            select artifact.Level).Distinct().ToListAsync(ct);

        // A level either has a verification discipline or it does not; the domain is the authority and is not
        // widened here. Customer and Interface have none, so their chain truthfully stops at Requirement.
        var withoutDiscipline = levels.Where(level => !HasVerificationDiscipline(level)).ToList();
        if (levels.Count > 0 && withoutDiscipline.Count == levels.Count)
        {
            var named = string.Join(" and ", withoutDiscipline.Select(x => x.ToString()).OrderBy(x => x));
            return new ArtifactThreadVerification(false,
                $"The {named} level has no verification discipline, so this thread has no test case, procedure or result.");
        }

        var coverage = await db.TestCoverage.AsNoTracking()
            .Where(x => requirementRevisionIds.Contains(x.RequirementRevisionId))
            .Select(x => new { x.ProcedureRevisionId, x.RequirementRevisionId, x.IsSuspect })
            .ToListAsync(ct);
        if (coverage.Count == 0) return new ArtifactThreadVerification(true, null);

        var directIds = coverage.Select(x => x.ProcedureRevisionId).Distinct().ToList();
        var caseLinks = await db.TestCaseProcedureLinks.AsNoTracking()
            .Where(x => directIds.Contains(x.CaseRevisionId))
            .Select(x => new { x.CaseRevisionId, x.ProcedureRevisionId, x.ExactLinkSuspectLifecycleId })
            .ToListAsync(ct);

        var allRevisionIds = directIds.Concat(caseLinks.Select(x => x.ProcedureRevisionId)).Distinct().ToList();
        var artifacts = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                               join procedure in db.TestProcedures.AsNoTracking()
                                   on revision.ProcedureId equals procedure.Id
                               where allRevisionIds.Contains(revision.Id) && procedure.ProjectId == projectId
                               select new
                               {
                                   revision.Id,
                                   procedure.BaseNumber,
                                   revision.Revision,
                                   revision.State,
                                   procedure.Level,
                                   procedure.ArtifactKind,
                                   ArtifactId = procedure.Id,
                               }).ToListAsync(ct);

        var titles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(
            db, artifacts.Select(x => x.Id).Distinct().ToList(), ct);
        var present = artifacts.Select(x => x.Id).ToHashSet();

        foreach (var artifact in artifacts)
        {
            var isCase = artifact.ArtifactKind == VerificationArtifactKind.Case;
            nodes.Add(new ArtifactThreadNode(
                artifact.Id, isCase ? "Case" : "Procedure",
                isCase ? ArtifactThreadLane.Case : ArtifactThreadLane.Procedure,
                $"{artifact.BaseNumber}.{artifact.Revision:D2}",
                titles.TryGetValue(artifact.Id, out var title) ? title.Title : null,
                artifact.State.ToString(), artifact.Level.ToString(),
                IsFocal: (focalKind == ArtifactThreadFocalKind.Case || focalKind == ArtifactThreadFocalKind.Procedure)
                    && artifact.Id == focalId,
                artifact.ArtifactId, artifact.Revision));
        }

        foreach (var row in coverage)
        {
            if (present.Contains(row.ProcedureRevisionId))
                edges.Add(new ArtifactThreadEdge(row.RequirementRevisionId, row.ProcedureRevisionId,
                    "verified by", row.IsSuspect));
        }

        var caseLifecycles = await LifecycleStatesAsync(db, projectId,
            caseLinks.Where(x => x.ExactLinkSuspectLifecycleId is not null)
                .Select(x => x.ExactLinkSuspectLifecycleId!.Value).ToList(), ct);

        foreach (var link in caseLinks)
        {
            if (present.Contains(link.CaseRevisionId) && present.Contains(link.ProcedureRevisionId))
                edges.Add(new ArtifactThreadEdge(link.CaseRevisionId, link.ProcedureRevisionId, "run by",
                    SuspectFromLifecycle(link.ExactLinkSuspectLifecycleId, caseLifecycles)));
        }

        await AddResultsAndBuildsAsync(db, projectId, present, focalKind, focalId, nodes, edges, ct);
        return new ArtifactThreadVerification(true, null);
    }

    /// <summary>
    /// Lane 5, which holds both executions and builds.
    ///
    /// <para>
    /// Every recorded execution of a procedure in the thread is returned, not the latest one. Which run a
    /// reader cares about is a question about a build, and the build is on the canvas beside it — picking one
    /// here would be the "latest" resolution §5.3 rules out. The build edge is intra-lane, per the prototype's
    /// <c>['EXE-004821', 'FMS-1.5.0', 'evidence for']</c>.
    /// </para>
    /// </summary>
    private static async Task AddResultsAndBuildsAsync(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> procedureRevisionIds,
        ArtifactThreadFocalKind focalKind, Guid focalId,
        List<ArtifactThreadNode> nodes, List<ArtifactThreadEdge> edges, CancellationToken ct)
    {
        var executions = await db.TestExecutions.AsNoTracking()
            .Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId) && x.ProjectId == projectId)
            .Select(x => new
            {
                x.Id,
                x.ProcedureRevisionId,
                x.Outcome,
                x.SoftwareBuildId,
                x.ExecutedBy,
                x.ExecutedAt,
                x.RecordedAt,
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
                                  link.TestExecutionId,
                                  record.Id,
                                  record.OriginalFileName,
                                  record.ContentType,
                                  record.Size,
                                  record.Sha256,
                                  record.UploadedBy,
                                  record.UploadedAt,
                              }).ToListAsync(ct);

        var byExecution = evidence.GroupBy(x => x.TestExecutionId).ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<ArtifactThreadEvidence>)group
                .Select(x => new ArtifactThreadEvidence(x.Id, x.OriginalFileName, x.ContentType, x.Size,
                    x.Sha256, x.UploadedBy, x.UploadedAt))
                .OrderBy(x => x.FileName, StringComparer.Ordinal).ToList());

        foreach (var execution in ordered)
        {
            nodes.Add(new ArtifactThreadNode(
                execution.Id, "Execution", ArtifactThreadLane.ResultAndBuild,
                // Executions carry no controlled number in this domain: TestExecution has no BaseNumber, and the
                // prototype's "EXE-004821" is mockup text. Naming one here would invent an identifier the
                // certification record does not have, so the card is identified by outcome and who ran it.
                DisplayNumber: null, execution.ExecutedBy,
                execution.Outcome.ToString(), Level: null,
                IsFocal: focalKind == ArtifactThreadFocalKind.Execution && execution.Id == focalId,
                ArtifactId: null, Revision: null, Outcome: execution.Outcome.ToString(),
                Evidence: byExecution.TryGetValue(execution.Id, out var files) ? files : []));

            edges.Add(new ArtifactThreadEdge(execution.ProcedureRevisionId, execution.Id, "produced", false));
        }

        var buildIds = ordered.Where(x => x.SoftwareBuildId is not null)
            .Select(x => x.SoftwareBuildId!.Value).Distinct().ToList();
        if (buildIds.Count == 0) return;

        var builds = await db.SoftwareBuilds.AsNoTracking()
            .Where(x => buildIds.Contains(x.Id) && x.ProjectId == projectId)
            .Select(x => new { x.Id, x.BuildNumber, x.Description })
            .ToListAsync(ct);
        var knownBuilds = builds.Select(x => x.Id).ToHashSet();

        foreach (var build in builds)
        {
            nodes.Add(new ArtifactThreadNode(build.Id, "Build", ArtifactThreadLane.ResultAndBuild,
                build.BuildNumber, build.Description, State: null, Level: null,
                IsFocal: focalKind == ArtifactThreadFocalKind.Build && build.Id == focalId));
        }

        foreach (var execution in ordered)
        {
            if (execution.SoftwareBuildId is Guid buildId && knownBuilds.Contains(buildId))
                edges.Add(new ArtifactThreadEdge(execution.Id, buildId, "evidence for", false));
        }
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
