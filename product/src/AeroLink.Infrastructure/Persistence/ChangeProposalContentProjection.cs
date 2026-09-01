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
    Guid Id,
    string DisplayNumber,
    string Level,
    string Statement,
    bool IsProposed,
    string? LinkType = null,
    Guid? ChangeRequestId = null,
    string? ChangeRequestDisplayNumber = null);

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
    IReadOnlyList<ProposalAllocationTarget> AllocatedDownstream);

/// <summary>The proposed content of one change request: lane 1 and lane 2 of the Digital Thread's inside-a-change view.</summary>
public sealed record ChangeProposalContentResult(
    Guid ChangeRequestId,
    Guid ProjectId,
    string DisplayNumber,
    IReadOnlyList<ChangeProposalItem> Items);

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
    private sealed record BaseRevision(string BaseNumber, int Revision, Guid Id, string Statement);

    private sealed record MaterializedChild(
        Guid TargetRevisionId, Guid Id, string BaseNumber, int Revision, string Level, string Statement, string Type);

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
                                  artifact.BaseNumber, revision.Revision, revision.Id, revision.Statement))
                             .ToListAsync(ct);
            foreach (var row in rows) byBase[(row.BaseNumber, row.Revision)] = row;
        }

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

            items.Add(new ChangeProposalItem(
                change.Id,
                change.DisplayNumber,
                change.Level.ToString(),
                change.Kind.ToString(),
                change.Statement,
                superseded,
                resolved?.Revision,
                resolved?.Id,
                allocated.OrderBy(x => x.DisplayNumber, StringComparer.Ordinal).ToList()));
        }

        return new ChangeProposalContentResult(scr.Id, scr.ProjectId, scr.DisplayNumber, items);
    }

    private static string Display(string baseNumber, int revision)
        => $"{baseNumber}.{revision:D2}";

    private static IReadOnlyList<Guid> Upstream(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
