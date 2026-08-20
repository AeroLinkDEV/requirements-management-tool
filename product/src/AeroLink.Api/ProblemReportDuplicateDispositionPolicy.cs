using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Authoritative policy for the controlled conclusion that one Problem Report is represented by another.
///
/// New decisions always point directly to one existing, same-Project, non-Duplicate root. A report already
/// serving as a root cannot itself be dispositioned Duplicate, and a reopened report cannot append a second
/// current-looking target to its retained historical decision. Legacy chains and invalid links remain intact
/// and are diagnosed for reconciliation; this policy never rewrites historical evidence.
/// </summary>
public sealed class ProblemReportDuplicateDispositionPolicy(AeroLinkDbContext db)
{
    public const string PolicyName = "SameProjectNonDuplicateCanonicalRootV1";

    public async Task<ProblemReportDuplicateDecision> ValidateAsync(ProblemReport source, Guid targetId,
        CancellationToken ct)
    {
        if (targetId == Guid.Empty)
            return Refuse("pr_duplicate_target_required", "A Duplicate disposition requires a canonical Problem Report target.");
        if (targetId == source.Id)
            return Refuse("pr_duplicate_self_reference", "A Problem Report cannot be a duplicate of itself.");

        var target = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == targetId, ct);
        if (target is null || target.ProjectId != source.ProjectId)
            return Refuse("pr_duplicate_target_not_in_project", "The selected canonical Problem Report does not exist in this Project.");

        var links = await DuplicateLinksAsync(ct);
        var walk = Walk(targetId, source.Id, links);
        if (walk.ReachesSource)
            return Refuse("pr_duplicate_cycle", "That duplicate decision would create a direct or transitive cycle.");
        if (walk.InvalidGraph)
            return Refuse("pr_duplicate_target_not_canonical", "The selected Problem Report does not resolve to one canonical root.");

        if (links.Any(link => IsProblemReportLink(link)
                && link.ArtifactId == source.Id && link.ProblemReportId != source.Id))
            return Refuse("pr_duplicate_source_is_canonical", "This Problem Report already represents another duplicate and must remain its canonical root.");
        if (links.Any(link => link.ProblemReportId == source.Id))
            return Refuse("pr_duplicate_history_already_exists", "This reopened Problem Report already retains a historical Duplicate decision and cannot append a competing target.");
        if (target.State == ProblemReportState.Rejected || links.Any(link => link.ProblemReportId == target.Id))
            return Refuse("pr_duplicate_target_not_canonical", "A Duplicate target must be a non-Duplicate canonical Problem Report root.");

        return new(true, null, null);
    }

    /// <summary>
    /// Purpose-specific picker projection. It applies the same canonical-root invariants as the write policy,
    /// while the disposition command repeats them inside its serializable transaction at commit time.
    /// </summary>
    public async Task<IReadOnlyList<ProblemReport>> EligibleTargetsAsync(ProblemReport source, string? search,
        int limit, CancellationToken ct)
    {
        var links = await DuplicateLinksAsync(ct);
        if (links.Any(link => link.ProblemReportId == source.Id)
            || links.Any(link => IsProblemReportLink(link) && link.ArtifactId == source.Id
                && link.ProblemReportId != source.Id))
            return [];

        var nonCanonicalIds = links.Select(link => link.ProblemReportId).Distinct().ToList();
        var query = db.ProblemReports.AsNoTracking().Where(item => item.ProjectId == source.ProjectId
            && item.Id != source.Id && item.State != ProblemReportState.Rejected
            && !nonCanonicalIds.Contains(item.Id));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(item => item.ReportNumber.ToLower().Contains(term)
                || item.Title.ToLower().Contains(term));
        }
        return await query.OrderBy(item => item.ReportNumber).ThenBy(item => item.Revision)
            .Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
    }

    public async Task<ProblemReportDuplicateDiagnostic> DiagnoseAsync(ProblemReport source, CancellationToken ct)
    {
        var links = await DuplicateLinksAsync(ct);
        var outgoing = links.GroupBy(link => link.ProblemReportId)
            .ToDictionary(group => group.Key, group => group.ToList());
        if (!outgoing.TryGetValue(source.Id, out var firstLinks) || firstLinks.Count == 0)
            return Diagnostic("None", "No Duplicate disposition relationship is recorded.", [source.Id]);
        if (firstLinks.Count != 1)
            return Diagnostic("MultipleTargets", "More than one Duplicate target is recorded; reconciliation is required.", [source.Id]);

        var path = new List<Guid> { source.Id };
        var visited = new HashSet<Guid> { source.Id };
        var current = source.Id;
        ProblemReport? canonical = null;
        while (outgoing.TryGetValue(current, out var candidates) && candidates.Count > 0)
        {
            if (candidates.Count != 1)
                return Diagnostic("MultipleTargets", "The retained Duplicate chain branches to multiple targets; reconciliation is required.", path);
            var link = candidates[0];
            if (!IsProblemReportLink(link))
                return Diagnostic("InvalidTargetType", "A Duplicate relationship does not target a Problem Report.", path);
            var next = link.ArtifactId;
            path.Add(next);
            if (!visited.Add(next))
                return Diagnostic("Cycle", "The retained Duplicate relationships contain a cycle; historical rows were not rewritten.", path);
            var target = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == next, ct);
            if (target is null)
                return Diagnostic("DanglingTarget", "The retained Duplicate target no longer resolves to a Problem Report.", path);
            if (target.ProjectId != source.ProjectId)
                return Diagnostic("CrossProjectTarget", "The retained Duplicate target belongs to another Project.", path);
            canonical = target;
            current = next;
        }

        // The source has exactly one outgoing link and every traversed target was resolved above.
        if (canonical is null)
            throw new InvalidOperationException("A Duplicate relationship diagnostic must resolve a target.");
        if (canonical.State == ProblemReportState.Rejected)
            return Diagnostic("InvalidDuplicateTarget", "The retained target is marked Duplicate but does not resolve to another canonical record.",
                path, canonical);
        if (path.Count > 2)
            return Diagnostic("NonCanonicalChain", "The retained relationship uses a legacy Duplicate chain instead of one direct canonical root.",
                path, canonical);
        return Diagnostic(path.Count > 2 ? "NonCanonicalChain" : "Valid",
            "The retained Duplicate relationship resolves to one same-Project canonical root.", path, canonical);
    }

    private async Task<List<ProblemReportLink>> DuplicateLinksAsync(CancellationToken ct) =>
        await db.ProblemReportLinks.AsNoTracking()
            .Where(link => link.Relationship == ProblemReportRelationshipPolicy.DuplicateOf).ToListAsync(ct);

    private static DuplicateWalk Walk(Guid start, Guid sourceId, IEnumerable<ProblemReportLink> links)
    {
        var outgoing = links.GroupBy(link => link.ProblemReportId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var visited = new HashSet<Guid>();
        var current = start;
        while (true)
        {
            if (current == sourceId) return new(true, false);
            if (!visited.Add(current)) return new(false, true);
            if (!outgoing.TryGetValue(current, out var candidates) || candidates.Count == 0)
                return new(false, false);
            if (candidates.Count != 1 || !IsProblemReportLink(candidates[0]))
                return new(false, true);
            current = candidates[0].ArtifactId;
        }
    }

    private static bool IsProblemReportLink(ProblemReportLink link) =>
        string.Equals(link.ArtifactType, "ProblemReport", StringComparison.Ordinal);

    private static ProblemReportDuplicateDecision Refuse(string code, string error) => new(false, code, error);

    private static ProblemReportDuplicateDiagnostic Diagnostic(string status, string message,
        IReadOnlyList<Guid> path, ProblemReport? canonical = null) =>
        new(PolicyName, status, message, path, canonical?.Id,
            canonical is null ? null : $"{canonical.ReportNumber}.{canonical.Revision:D2}",
            canonical?.Title, canonical?.State.ToString());

    private sealed record DuplicateWalk(bool ReachesSource, bool InvalidGraph);
}

public sealed record ProblemReportDuplicateDecision(bool Accepted, string? Code, string? Error);

public sealed record ProblemReportDuplicateDiagnostic(string Policy, string Status, string Message,
    IReadOnlyList<Guid> Path, Guid? CanonicalTargetId, string? CanonicalTargetIdentifier,
    string? CanonicalTargetTitle, string? CanonicalTargetState);
