using AeroLink.Domain.Common;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Atomic, source-bound acceptance of one immutable Code evidence decision.</summary>
public sealed class CodeEvidenceAcceptanceService(AeroLinkDbContext db)
{
    private const int MaxContributions = 50;

    public async Task<CodeEvidenceAcceptanceResult> AcceptAsync(
        ProjectControlledWriteScope scope,
        CodeEvidenceAcceptanceCommand command,
        IReadOnlyDictionary<Guid, CodeEvidenceMergeObservation> mergeObservations,
        ILadderPolicy ladderPolicy,
        string actor,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ProjectControlledWriteScope.Require(db, command.ProjectId, scope);
        if (command.ProjectId == Guid.Empty || command.ReleaseId == Guid.Empty || command.ExpectedBaselineId == Guid.Empty
            || command.RequirementArtifactId == Guid.Empty || command.RequirementRevisionId == Guid.Empty)
            throw new DomainException("Project, expected baseline, release and exact requirement identities are required.");
        if (command.ExpectedSelectorVersion < 0)
            throw new DomainException("The expected evidence selector version cannot be negative.");
        if (command.ExpectedLegacyRecordId == Guid.Empty)
            throw new DomainException("The expected legacy evidence identity cannot be empty.");

        var contributions = command.Contributions ?? [];
        if (contributions.Count > MaxContributions)
            throw new DomainException("An evidence decision cannot contain more than 50 contributions.");
        if (contributions.Any(x => x.RelationshipId == Guid.Empty || x.ExpectedRelationshipVersion < 1 || !Enum.IsDefined(x.Kind)))
            throw new DomainException("Every contribution requires an exact relationship identity, kind and expected version.");
        if (contributions.GroupBy(x => (x.Kind, x.RelationshipId)).Any(x => x.Count() != 1))
            throw new DomainException("An evidence decision cannot repeat a relationship contribution.");

        var release = await db.Releases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == command.ReleaseId && x.ProjectId == command.ProjectId, ct)
            ?? throw new KeyNotFoundException("The implementation release was not found.");
        if (release.IsReleased)
            throw new DomainException("The implementation release is released and its Code evidence is immutable.");
        if (await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.ProjectId == command.ProjectId
                && x.ReleaseId == command.ReleaseId
                && (x.State == ReleaseCampaignState.InReview || x.State == ReleaseCampaignState.Released), ct))
            throw new DomainException("The release package is frozen or released and cannot accept Code evidence.");

        var campaign = await db.ReleaseCampaigns.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == command.ProjectId && x.ReleaseId == command.ReleaseId, ct)
            ?? throw new DomainException("The release has no controlled review campaign.");
        if (campaign.BaselineId != command.ExpectedBaselineId)
            throw new DomainException("The expected materialized baseline changed; refresh before accepting Code evidence.");
        var baseline = await db.CandidateBaselines.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == command.ExpectedBaselineId && x.ProjectId == command.ProjectId
                && x.ReleaseId == command.ReleaseId && x.State != CandidateBaselineState.Draft
                && x.RequirementsMaterializedAt != null, ct)
            ?? throw new DomainException("Materialize the exact release baseline before accepting Code evidence.");
        var required = await CodeTraceabilityProjection.RequiredAsync(db, command.ProjectId, command.ReleaseId,
            baseline.Id, ladderPolicy, ct);
        if (!required.Any(x => x.ArtifactId == command.RequirementArtifactId && x.RevisionId == command.RequirementRevisionId))
            throw new DomainException("The exact requirement revision is not required by this materialized release baseline.");
        var exactTarget = await CodeRelationshipTargetResolver.ResolveAsync(db, command.ProjectId,
            CodeRelationshipTargetKind.RequirementRevision, command.RequirementRevisionId, ct);
        if (exactTarget is null || exactTarget.OwningIdentityId != command.RequirementArtifactId)
            throw new DomainException("The exact requirement revision target is no longer available in this project.");

        var legacy = await db.CodeTraceabilityRecords.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == command.ProjectId && x.ReleaseId == command.ReleaseId
                && x.RequirementArtifactId == command.RequirementArtifactId
                && x.RequirementRevisionId == command.RequirementRevisionId, ct);
        if (command.ExpectedLegacyRecordId.HasValue)
        {
            if (legacy is null || legacy.Id != command.ExpectedLegacyRecordId.Value)
                throw new DomainException("The expected legacy Code evidence identity changed; refresh before accepting.");
        }
        else if (legacy is not null)
            throw new DomainException("Replacing legacy Code evidence requires its explicit expected identity.");

        var currentSource = await db.GitLabCurrentSourceSelections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == command.ProjectId && x.ReleaseId == command.ReleaseId, ct);
        GitLabSourceSelectionEvent? selectionEvent = null;
        GitLabSourceSnapshot? sourceSnapshot = null;
        if (command.Disposition == CodeEvidenceDisposition.NoCodeChangeRequired)
        {
            if (string.IsNullOrWhiteSpace(command.NoCodeChangeRationale))
                throw new DomainException("A no-code disposition requires a rationale.");
            if (command.ExpectedConfigurationVersion.HasValue || command.ExpectedSourceSelectionEventId.HasValue
                || command.ExpectedSourceSnapshotId.HasValue || command.ExpectedSourceSelectionVersion.HasValue)
                throw new DomainException("A no-code disposition cannot carry GitLab source expectations.");
            if (contributions.Count != 0)
                throw new DomainException("A no-code disposition cannot carry GitLab contributions.");
        }
        else if (command.Disposition == CodeEvidenceDisposition.GitLabContributions)
        {
            if (contributions.Count == 0)
                throw new DomainException("A GitLab disposition requires at least one contribution.");
            if (!command.ExpectedConfigurationVersion.HasValue || command.ExpectedConfigurationVersion.Value < 1
                || !command.ExpectedSourceSelectionEventId.HasValue || !command.ExpectedSourceSnapshotId.HasValue
                || !command.ExpectedSourceSelectionVersion.HasValue || command.ExpectedSourceSelectionVersion.Value < 1)
                throw new DomainException("A GitLab disposition requires the expected repository and source-selection identities.");
            var configuration = await db.ProjectRepositoryConfigurations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProjectId == command.ProjectId
                    && x.Status == ProjectRepositorySetupStatus.Verified
                    && x.Version == command.ExpectedConfigurationVersion.Value, ct)
                ?? throw new DomainException("The verified repository configuration changed; refresh before accepting.");
            if (currentSource is null || currentSource.Version != command.ExpectedSourceSelectionVersion.Value
                || currentSource.SelectionEventId != command.ExpectedSourceSelectionEventId.Value
                || currentSource.SourceSnapshotId != command.ExpectedSourceSnapshotId.Value)
                throw new DomainException("The selected source changed; refresh and explicitly reconfirm the source before accepting.");
            selectionEvent = await db.GitLabSourceSelectionEvents.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == command.ProjectId
                && x.ReleaseId == command.ReleaseId && x.Id == command.ExpectedSourceSelectionEventId.Value
                && x.SourceSnapshotId == command.ExpectedSourceSnapshotId.Value, ct)
                ?? throw new DomainException("The expected source selection event is not part of this release.");
            if (selectionEvent.ResultingVersion != command.ExpectedSourceSelectionVersion.Value)
                throw new DomainException("The expected source selection version does not match its event.");
            sourceSnapshot = await db.GitLabSourceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == command.ProjectId
                && x.Id == command.ExpectedSourceSnapshotId.Value, ct)
                ?? throw new DomainException("The expected source snapshot is not part of this project.");
            if (sourceSnapshot.RepositoryConfigurationId != configuration.Id
                || sourceSnapshot.ConfigurationVersion != configuration.Version
                || sourceSnapshot.RemoteProjectId != configuration.RemoteProjectId
                || !string.Equals(sourceSnapshot.PathWithNamespace, configuration.RemotePathWithNamespace, StringComparison.Ordinal))
                throw new DomainException("The expected source snapshot is not bound to the verified repository.");
            if (selectionEvent.SourceSnapshotId != sourceSnapshot.Id)
                throw new DomainException("The expected source event and snapshot do not match.");
        }
        else throw new DomainException("The Code evidence disposition is not supported.");

        var mergeIds = contributions.Where(x => x.Kind == CodeEvidenceContributionKind.MergeRequest)
            .Select(x => x.RelationshipId).ToHashSet();
        var fileIds = contributions.Where(x => x.Kind == CodeEvidenceContributionKind.File)
            .Select(x => x.RelationshipId).ToHashSet();
        var mergeRows = await db.GitLabMergeRequestRelationships
            .Where(x => mergeIds.Contains(x.Id) && x.ProjectId == command.ProjectId).ToListAsync(ct);
        var fileRows = await db.GitLabFileRelationships
            .Where(x => fileIds.Contains(x.Id) && x.ProjectId == command.ProjectId).ToListAsync(ct);
        if (mergeRows.Count != mergeIds.Count || fileRows.Count != fileIds.Count)
            throw new KeyNotFoundException("One or more Code contribution relationships were not found.");
        var merges = mergeRows.ToDictionary(x => x.Id);
        var files = fileRows.ToDictionary(x => x.Id);
        foreach (var request in contributions)
        {
            if (request.Kind == CodeEvidenceContributionKind.MergeRequest)
            {
                var row = merges[request.RelationshipId];
                ValidateRelationship(row.ProjectId, row.ReleaseId, row.IsActive, row.Version,
                    request.ExpectedRelationshipVersion, command);
                if (!string.Equals(row.InstanceBaseUrl, sourceSnapshot!.InstanceBaseUrl, StringComparison.OrdinalIgnoreCase)
                    || row.RemoteProjectId != sourceSnapshot.RemoteProjectId
                    || !string.Equals(row.RepositoryPathSnapshot, sourceSnapshot.PathWithNamespace, StringComparison.Ordinal)
                    || row.Meaning != CodeRelationshipMeaning.Implements
                    || row.TargetKind != CodeRelationshipTargetKind.RequirementRevision
                    || row.TargetIdentityId != command.RequirementRevisionId || row.TargetOwnerIdentityId != command.RequirementArtifactId)
                    throw new DomainException("Every accepted merge-request contribution must bind the configured repository and exact requirement revision.");
                if (!mergeObservations.TryGetValue(row.Id, out var observation)
                    || observation.RelationshipId != row.Id || observation.ExpectedRelationshipVersion != row.Version)
                    throw new DomainException("A merge-request contribution is missing its provider observation.");
            }
            else
            {
                var row = files[request.RelationshipId];
                ValidateRelationship(row.ProjectId, row.ReleaseId, row.IsActive, row.Version,
                    request.ExpectedRelationshipVersion, command);
                if (row.SourceSnapshotId != sourceSnapshot!.Id
                    || !string.Equals(row.InstanceBaseUrl, sourceSnapshot.InstanceBaseUrl, StringComparison.OrdinalIgnoreCase)
                    || row.RemoteProjectId != sourceSnapshot.RemoteProjectId
                    || row.CommitSha != sourceSnapshot.CommitSha || row.TargetKind != CodeRelationshipTargetKind.RequirementRevision
                    || row.Meaning != CodeRelationshipMeaning.Implements
                    || row.TargetIdentityId != command.RequirementRevisionId || row.TargetOwnerIdentityId != command.RequirementArtifactId)
                    throw new DomainException("Every accepted file contribution must bind the exact selected source commit and requirement revision.");
            }
        }

        var evidence = command.Disposition == CodeEvidenceDisposition.NoCodeChangeRequired
            ? new CodeEvidenceDispositionSet(command.ProjectId, command.ReleaseId, command.RequirementArtifactId,
                command.RequirementRevisionId, command.Disposition, command.NoCodeChangeRationale,
                null, null, command.ExpectedLegacyRecordId, actor, now)
            : CodeEvidenceDispositionSet.CreateGitLab(command.ProjectId, command.ReleaseId,
                command.RequirementArtifactId, command.RequirementRevisionId, selectionEvent!, sourceSnapshot!,
                command.ExpectedLegacyRecordId, actor, now);
        db.CodeEvidenceDispositionSets.Add(evidence);

        foreach (var request in contributions)
        {
            if (request.Kind == CodeEvidenceContributionKind.MergeRequest)
            {
                var row = merges[request.RelationshipId];
                var observed = mergeObservations[row.Id];
                var target = Target(row.TargetKind, row.TargetIdentityId, row.TargetOwnerIdentityId,
                    row.TargetRevisionNumber, row.TargetStableIdentity, row.TargetDisplaySnapshot);
                db.CodeEvidenceContributions.Add(new CodeEvidenceContribution(evidence.Id, command.ProjectId,
                    command.ReleaseId, command.RequirementArtifactId, command.RequirementRevisionId, sourceSnapshot!.Id,
                    request.Kind, row.Id, row.InstanceBaseUrl, row.RemoteProjectId, row.RepositoryPathSnapshot,
                    row.MergeRequestIid, row.MergeRequestId, row.MergeRequestUrlSnapshot, row.MergeRequestTitleSnapshot,
                    sourceSnapshot.CommitSha, null, null, null, target, actor, now,
                    observed.MergeResultSha, observed.MergeResultKind, observed.MergedAt, observed.ProviderObservedAt));
            }
            else
            {
                var row = files[request.RelationshipId];
                var target = Target(row.TargetKind, row.TargetIdentityId, row.TargetOwnerIdentityId,
                    row.TargetRevisionNumber, row.TargetStableIdentity, row.TargetDisplaySnapshot);
                db.CodeEvidenceContributions.Add(new CodeEvidenceContribution(evidence.Id, command.ProjectId,
                    command.ReleaseId, command.RequirementArtifactId, command.RequirementRevisionId, sourceSnapshot!.Id,
                    request.Kind, row.Id, row.InstanceBaseUrl, row.RemoteProjectId, sourceSnapshot!.PathWithNamespace,
                    row.MergeRequestIid, null, null, null, sourceSnapshot.CommitSha, row.Path, row.StartLine, row.EndLine,
                    target, actor, now));
            }
        }

        var selector = await db.CodeEvidenceCurrentSelectors.SingleOrDefaultAsync(x => x.ProjectId == command.ProjectId
            && x.ReleaseId == command.ReleaseId && x.RequirementArtifactId == command.RequirementArtifactId
            && x.RequirementRevisionId == command.RequirementRevisionId, ct);
        if (selector is null)
        {
            if (command.ExpectedSelectorVersion != 0)
                throw new DomainException("The expected evidence selector is absent; refresh before accepting.");
            selector = new CodeEvidenceCurrentSelector(command.ProjectId, command.ReleaseId,
                command.RequirementArtifactId, command.RequirementRevisionId, evidence.Id, actor, now);
            db.CodeEvidenceCurrentSelectors.Add(selector);
        }
        else
        {
            selector.Move(command.ExpectedSelectorVersion, evidence.Id, actor, now);
        }
        return new(evidence.Id, selector.Id, selector.Version, command.Disposition);
    }

    private static void ValidateRelationship(Guid projectId, Guid releaseId, bool active, long version,
        long expectedVersion, CodeEvidenceAcceptanceCommand command)
    {
        if (!active || version != expectedVersion)
            throw new DomainException("A Code contribution relationship changed or is withdrawn; refresh before accepting.");
        if (projectId != command.ProjectId || releaseId != command.ReleaseId)
            throw new DomainException("A Code contribution relationship is outside the exact acceptance scope.");
    }

    private static CodeRelationshipTarget Target(CodeRelationshipTargetKind kind, Guid id, Guid? owner,
        int? revision, string stable, string display)
    {
        var result = kind switch
        {
            CodeRelationshipTargetKind.RequirementRevision when owner.HasValue && revision.HasValue =>
                CodeRelationshipTarget.ForRequirementRevision(id, owner.Value, revision.Value, display),
            CodeRelationshipTargetKind.ChangeRequestRevision when revision.HasValue =>
                CodeRelationshipTarget.ForChangeRequestRevision(id, revision.Value, display),
            CodeRelationshipTargetKind.RequirementProposal when owner.HasValue =>
                CodeRelationshipTarget.ForRequirementProposal(id, owner.Value, display),
            CodeRelationshipTargetKind.ProblemReportRevision when owner.HasValue && revision.HasValue =>
                CodeRelationshipTarget.ForProblemReportSnapshot(id, owner.Value, revision.Value, display),
            _ => throw new DomainException($"The stored Code target identity '{stable}' is incomplete.")
        };
        if (!string.Equals(result.StableIdentity, stable, StringComparison.Ordinal))
            throw new DomainException("The stored Code target identity no longer matches the exact target snapshot.");
        return result;
    }
}

public sealed record CodeEvidenceAcceptanceCommand(Guid ProjectId, Guid ReleaseId,
    Guid ExpectedBaselineId, Guid RequirementArtifactId, Guid RequirementRevisionId, CodeEvidenceDisposition Disposition,
    long ExpectedSelectorVersion, Guid? ExpectedLegacyRecordId, long? ExpectedConfigurationVersion,
    Guid? ExpectedSourceSelectionEventId, Guid? ExpectedSourceSnapshotId, long? ExpectedSourceSelectionVersion,
    IReadOnlyList<CodeEvidenceContributionRequest> Contributions, string? NoCodeChangeRationale);

public sealed record CodeEvidenceContributionRequest(CodeEvidenceContributionKind Kind,
    Guid RelationshipId, long ExpectedRelationshipVersion);

public sealed record CodeEvidenceMergeObservation(Guid RelationshipId, long ExpectedRelationshipVersion,
    string MergeResultSha, GitLabMergeResultKind MergeResultKind, DateTimeOffset MergedAt,
    DateTimeOffset ProviderObservedAt);

public sealed record CodeEvidenceAcceptanceResult(Guid EvidenceSetId, Guid SelectorId,
    long SelectorVersion, CodeEvidenceDisposition Disposition);
