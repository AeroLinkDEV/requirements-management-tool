using AeroLink.Domain.Integrations;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public enum CurrentCodeEvidenceState { LegacyAccepted, Accepted, Invalidated, SourceChanged, InvalidIdentity }

public sealed record CurrentCodeEvidence(Guid RequirementArtifactId, Guid RequirementRevisionId,
    CurrentCodeEvidenceState State, CodeTraceabilityRecord? LegacyRecord, CodeEvidenceDispositionSet? EvidenceSet,
    CodeEvidenceCurrentSelector? Selector, IReadOnlyList<CodeEvidenceContribution> Contributions,
    string? InvalidationRationale = null)
{
    public bool CountsAsImplementation => State is CurrentCodeEvidenceState.LegacyAccepted or CurrentCodeEvidenceState.Accepted;
}

/// <summary>
/// One current disposition per exact requirement/build. An explicit new selector always wins, including
/// when its decision is invalidated or its source is no longer selected. Retained history is never fallback.
/// This does not choose the gate denominator: use CodeTraceabilityProjection.RequiredAsync for that.
/// </summary>
public static class CurrentCodeEvidenceProjection
{
    public static async Task<IReadOnlyList<CurrentCodeEvidence>> ForReleaseAsync(AeroLinkDbContext db,
        Guid projectId, Guid releaseId, CancellationToken ct)
    {
        var legacy = await db.CodeTraceabilityRecords.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId).ToListAsync(ct);
        var selectors = await db.CodeEvidenceCurrentSelectors.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId).ToListAsync(ct);
        var setIds = selectors.Select(x => x.EvidenceSetId).ToArray();
        var selectedRevisionIds = selectors.Select(x => x.RequirementRevisionId).ToArray();
        var exactRequirements = (await (from revision in db.RequirementRevisions.AsNoTracking()
                                       where selectedRevisionIds.Contains(revision.Id)
                                       join artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId)
                                           on revision.ArtifactId equals artifact.Id
                                       select new { ArtifactId = artifact.Id, RevisionId = revision.Id }).ToListAsync(ct))
            .Select(x => (x.ArtifactId, x.RevisionId)).ToHashSet();
        var sets = await db.CodeEvidenceDispositionSets.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId && setIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var contributions = await db.CodeEvidenceContributions.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId && setIds.Contains(x.EvidenceSetId)).ToListAsync(ct);
        var invalidations = await db.CodeEvidenceInvalidations.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId && setIds.Contains(x.EvidenceSetId))
            .ToListAsync(ct);
        var invalidated = invalidations.Select(x => x.EvidenceSetId).ToHashSet();
        var invalidationReasons = invalidations.OrderByDescending(x => x.InvalidatedAt).ThenBy(x => x.Id)
            .GroupBy(x => x.EvidenceSetId).ToDictionary(x => x.Key, x => x.First().Rationale);
        var currentSource = await db.GitLabCurrentSourceSelections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.ReleaseId == releaseId, ct);
        var source = currentSource is null ? null : await db.GitLabSourceSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == currentSource.SourceSnapshotId, ct);
        var result = new List<CurrentCodeEvidence>();
        var selectedRevisions = selectors.Select(x => x.RequirementRevisionId).ToHashSet();
        result.AddRange(legacy.Where(x => !selectedRevisions.Contains(x.RequirementRevisionId))
            .Select(x => new CurrentCodeEvidence(x.RequirementArtifactId, x.RequirementRevisionId,
                CurrentCodeEvidenceState.LegacyAccepted, x, null, null, [])));
        var bySet = contributions.ToLookup(x => x.EvidenceSetId);
        foreach (var selector in selectors)
        {
            sets.TryGetValue(selector.EvidenceSetId, out var set);
            var items = bySet[selector.EvidenceSetId].OrderBy(x => x.Id).ToArray();
            var state = State(selector, set, items, invalidated, currentSource, source);
            if (state != CurrentCodeEvidenceState.Invalidated
                && !exactRequirements.Contains((selector.RequirementArtifactId, selector.RequirementRevisionId)))
                state = CurrentCodeEvidenceState.InvalidIdentity;
            result.Add(new(selector.RequirementArtifactId, selector.RequirementRevisionId, state,
                null, set, selector, items, invalidationReasons.GetValueOrDefault(selector.EvidenceSetId)));
        }
        return result.OrderBy(x => x.RequirementRevisionId).ToArray();
    }

    private static CurrentCodeEvidenceState State(CodeEvidenceCurrentSelector selector,
        CodeEvidenceDispositionSet? set, IReadOnlyList<CodeEvidenceContribution> items, HashSet<Guid> invalidated,
        GitLabCurrentSourceSelection? current, GitLabSourceSnapshot? source)
    {
        if (set is null || set.RequirementArtifactId != selector.RequirementArtifactId
            || set.RequirementRevisionId != selector.RequirementRevisionId)
            return CurrentCodeEvidenceState.InvalidIdentity;
        if (invalidated.Contains(set.Id)) return CurrentCodeEvidenceState.Invalidated;
        if (set.Disposition == CodeEvidenceDisposition.NoCodeChangeRequired)
            return items.Count == 0 && set.SourceSnapshotId is null && set.SourceSelectionEventId is null
                && !string.IsNullOrWhiteSpace(set.NoCodeChangeRationale)
                ? CurrentCodeEvidenceState.Accepted : CurrentCodeEvidenceState.InvalidIdentity;
        if (set.Disposition != CodeEvidenceDisposition.GitLabContributions)
            return CurrentCodeEvidenceState.InvalidIdentity;
        if (current is null || current.SelectionEventId != set.SourceSelectionEventId
            || current.SourceSnapshotId != set.SourceSnapshotId)
            return CurrentCodeEvidenceState.SourceChanged;
        if (source is null || items.Count == 0 || items.Any(x =>
                x.RequirementArtifactId != set.RequirementArtifactId || x.RequirementRevisionId != set.RequirementRevisionId
                || x.SourceSnapshotId != source.Id || x.CommitSha != source.CommitSha
                || x.InstanceBaseUrl != source.InstanceBaseUrl || x.RemoteProjectId != source.RemoteProjectId
                || x.TargetKind != CodeRelationshipTargetKind.RequirementRevision
                || x.TargetIdentityId != set.RequirementRevisionId || x.TargetOwnerIdentityId != set.RequirementArtifactId
                || (x.ContributionKind == CodeEvidenceContributionKind.MergeRequest
                    && (x.MergeResultSha is null || x.MergeResultKind is null || x.MergedAt is null || x.ProviderObservedAt is null))))
            return CurrentCodeEvidenceState.InvalidIdentity;
        return CurrentCodeEvidenceState.Accepted;
    }
}
