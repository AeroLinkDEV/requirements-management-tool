using System.Text.Json;
using System.Text.RegularExpressions;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Integrations;

public enum CodeRelationshipTargetKind
{
    RequirementRevision,
    ChangeRequestRevision,
    ProblemReportRevision,
    RequirementProposal,
}

public enum CodeRelationshipMeaning
{
    Implements,
    Addresses,
    RelatedContext,
}

public enum CodeRelationshipKind
{
    MergeRequest,
    File,
}

public enum CodeRelationshipEventKind
{
    Added,
    Withdrawn,
    ReAdded,
}

public enum CodeEvidenceDisposition
{
    GitLabContributions,
    NoCodeChangeRequired,
}

public enum CodeEvidenceContributionKind
{
    MergeRequest,
    File,
}

public enum GitLabMergeResultKind { MergeCommit, SquashCommit }

internal static class CodeEvidenceValidation
{
    private static readonly Regex ShaPattern = new("^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    public static string Required(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException(message) : value.Trim();

    public static string Sha(string? value, string message)
    {
        var result = Required(value, message).ToLowerInvariant();
        if (!ShaPattern.IsMatch(result)) throw new DomainException(message + " It must be a full hexadecimal SHA.");
        return result;
    }

    public static string Sha256(string? value)
    {
        var result = Required(value, "A manifest hash is required.").ToLowerInvariant();
        if (!Sha256Pattern.IsMatch(result)) throw new DomainException("The manifest hash must be a 64-character SHA-256 value.");
        return result;
    }

    public static string HttpsUrl(string? value, string message)
    {
        var result = Required(value, message);
        if (!Uri.TryCreate(result, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new DomainException(message + " It must be an HTTPS URL without credentials, query, or fragment.");
        var path = uri.AbsolutePath.Trim('/');
        if (path.Split('/').Any(segment => segment is "." or "..") || path.Contains('\\'))
            throw new DomainException(message + " It must not contain unsafe path segments.");
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"https://{uri.Host.ToLowerInvariant()}{port}{(path.Length == 0 ? string.Empty : "/" + path)}";
    }

    public static string RepositoryPath(string? value, string message)
    {
        var result = Required(value, message).Trim('/');
        if (result.Length == 0 || result.Contains('\\') || result.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new DomainException(message + " It must be a safe namespace path.");
        return result;
    }

    public static string RelativeFilePath(string? value)
    {
        var result = Required(value, "A repository file path is required.").Trim('/');
        if (result.Length == 0 || result.Contains('\\') || result.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new DomainException("A repository file path must be a safe relative path.");
        return result;
    }

    public static string Actor(string? value) => Required(value, "An attributable actor is required.");

    public static void Id(Guid id, string message)
    {
        if (id == Guid.Empty) throw new DomainException(message);
    }

    public static void Enum<T>(T value, string message) where T : struct, System.Enum
    {
        if (!System.Enum.IsDefined(value)) throw new DomainException(message);
    }

    public static void Lines(int? startLine, int? endLine)
    {
        if (startLine is null && endLine is null) return;
        if (startLine is null || endLine is null || startLine < 1 || endLine < startLine)
            throw new DomainException("A file line range must contain ordered positive start and end lines.");
    }

    public static string Json(string? value, string message)
    {
        var result = Required(value, message);
        try
        {
            using var document = JsonDocument.Parse(result);
            if (document.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                throw new DomainException(message + " It must be a JSON object or array.");
        }
        catch (JsonException)
        {
            throw new DomainException(message + " It must be valid JSON.");
        }
        return result;
    }

    public static string EvidenceIds(IEnumerable<Guid>? ids, int formatVersion)
    {
        var values = (ids ?? []).ToArray();
        if (values.Any(x => x == Guid.Empty) || values.Distinct().Count() != values.Length)
            throw new DomainException("Code manifest evidence references must contain distinct non-empty evidence identities.");
        if (formatVersion == 1 && values.Length != 0)
            throw new DomainException("A v1 Code manifest cannot carry v2 evidence references.");
        return JsonSerializer.Serialize(values.OrderBy(x => x).ToArray());
    }
}

/// <summary>Exact repository identity and commit observed from a verified project configuration.</summary>
public sealed class GitLabSourceSnapshot
{
    private GitLabSourceSnapshot() { }

    public GitLabSourceSnapshot(Guid projectId, Guid repositoryConfigurationId, string instanceBaseUrl,
        long remoteProjectId, string pathWithNamespace, string commitSha, string? friendlyRef,
        string recordedBy, DateTimeOffset recordedAt, long configurationVersion)
    {
        CodeEvidenceValidation.Id(projectId, "A source snapshot requires a project.");
        CodeEvidenceValidation.Id(repositoryConfigurationId, "A source snapshot requires its repository configuration.");
        if (remoteProjectId <= 0) throw new DomainException("A source snapshot requires a positive remote project identity.");
        if (configurationVersion < 1) throw new DomainException("A source snapshot requires a positive repository configuration version.");
        Id = Guid.NewGuid(); ProjectId = projectId; RepositoryConfigurationId = repositoryConfigurationId;
        InstanceBaseUrl = CodeEvidenceValidation.HttpsUrl(instanceBaseUrl, "A source snapshot requires its HTTPS instance URL.");
        RemoteProjectId = remoteProjectId;
        PathWithNamespace = CodeEvidenceValidation.RepositoryPath(pathWithNamespace, "A source snapshot requires its repository path.");
        CommitSha = CodeEvidenceValidation.Sha(commitSha, "A source snapshot requires its exact commit SHA.");
        FriendlyRef = string.IsNullOrWhiteSpace(friendlyRef) ? null : friendlyRef.Trim();
        RecordedBy = CodeEvidenceValidation.Actor(recordedBy); RecordedAt = recordedAt;
        ConfigurationVersion = configurationVersion;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid RepositoryConfigurationId { get; private set; }
    public string InstanceBaseUrl { get; private set; } = string.Empty;
    public long RemoteProjectId { get; private set; }
    public string PathWithNamespace { get; private set; } = string.Empty;
    public string CommitSha { get; private set; } = string.Empty;
    public string? FriendlyRef { get; private set; }
    public string RecordedBy { get; private set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; private set; }
    public long ConfigurationVersion { get; private set; }
}

/// <summary>An immutable release source selection event. The event is the binding that makes A to B to A a new confirmation.</summary>
public sealed class GitLabSourceSelectionEvent
{
    private GitLabSourceSelectionEvent() { }

    public GitLabSourceSelectionEvent(Guid projectId, Guid releaseId, Guid sourceSnapshotId,
        long expectedCurrentVersion, string selectedBy, DateTimeOffset selectedAt)
    {
        CodeEvidenceValidation.Id(projectId, "A source selection requires a project.");
        CodeEvidenceValidation.Id(releaseId, "A source selection requires a release.");
        CodeEvidenceValidation.Id(sourceSnapshotId, "A source selection requires a source snapshot.");
        if (expectedCurrentVersion < 0) throw new DomainException("A source selection version cannot be negative.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; SourceSnapshotId = sourceSnapshotId;
        ExpectedCurrentVersion = expectedCurrentVersion; ResultingVersion = expectedCurrentVersion + 1;
        SelectedBy = CodeEvidenceValidation.Actor(selectedBy); SelectedAt = selectedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid SourceSnapshotId { get; private set; }
    public long ExpectedCurrentVersion { get; private set; }
    public long ResultingVersion { get; private set; }
    public string SelectedBy { get; private set; } = string.Empty;
    public DateTimeOffset SelectedAt { get; private set; }
}

/// <summary>The single current source pointer for a project/release. Version is an optimistic concurrency token.</summary>
public sealed class GitLabCurrentSourceSelection
{
    private GitLabCurrentSourceSelection() { }

    public GitLabCurrentSourceSelection(Guid projectId, Guid releaseId, Guid sourceSnapshotId, Guid selectionEventId,
        string changedBy, DateTimeOffset changedAt)
    {
        CodeEvidenceValidation.Id(projectId, "A current source selection requires a project.");
        CodeEvidenceValidation.Id(releaseId, "A current source selection requires a release.");
        CodeEvidenceValidation.Id(sourceSnapshotId, "A current source selection requires a source snapshot.");
        CodeEvidenceValidation.Id(selectionEventId, "A current source selection requires a selection event.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; SourceSnapshotId = sourceSnapshotId;
        SelectionEventId = selectionEventId; Version = 1; ChangedBy = CodeEvidenceValidation.Actor(changedBy); ChangedAt = changedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid SourceSnapshotId { get; private set; }
    public Guid SelectionEventId { get; private set; }
    public long Version { get; private set; }
    public string ChangedBy { get; private set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; private set; }

    public void Move(long expectedVersion, Guid sourceSnapshotId, Guid selectionEventId, string changedBy, DateTimeOffset changedAt)
    {
        if (expectedVersion < 1 || expectedVersion != Version)
            throw new DomainException("Another current source selection was saved. Refresh before changing it.");
        CodeEvidenceValidation.Id(sourceSnapshotId, "A current source selection requires a source snapshot.");
        CodeEvidenceValidation.Id(selectionEventId, "A current source selection requires a selection event.");
        SourceSnapshotId = sourceSnapshotId; SelectionEventId = selectionEventId;
        ChangedBy = CodeEvidenceValidation.Actor(changedBy); ChangedAt = changedAt; Version++;
    }
}

/// <summary>Immutable typed identity and display capture for a relationship target. It intentionally has no target FK.</summary>
public sealed record CodeRelationshipTarget(
    CodeRelationshipTargetKind Kind,
    Guid ExactIdentityId,
    Guid? OwningIdentityId,
    int? RevisionNumber,
    string StableIdentity,
    string DisplaySnapshot)
{
    public static CodeRelationshipTarget ForRequirementRevision(Guid revisionId, Guid artifactId, int revision, string displaySnapshot) =>
        Create(CodeRelationshipTargetKind.RequirementRevision, revisionId, artifactId, revision, displaySnapshot);

    public static CodeRelationshipTarget ForChangeRequestRevision(Guid revisionId, Guid changeRequestId, int revision, string displaySnapshot) =>
        Create(CodeRelationshipTargetKind.ChangeRequestRevision, revisionId, changeRequestId, revision, displaySnapshot);

    public static CodeRelationshipTarget ForProblemReportRevision(Guid revisionId, Guid problemReportId, int revision, string displaySnapshot) =>
        Create(CodeRelationshipTargetKind.ProblemReportRevision, revisionId, problemReportId, revision, displaySnapshot);

    public static CodeRelationshipTarget ForRequirementProposal(Guid proposalId, Guid? changeRequestId, string displaySnapshot) =>
        Create(CodeRelationshipTargetKind.RequirementProposal, proposalId, changeRequestId, null, displaySnapshot);

    private static CodeRelationshipTarget Create(CodeRelationshipTargetKind kind, Guid exactIdentityId, Guid? ownerId,
        int? revision, string displaySnapshot)
    {
        CodeEvidenceValidation.Enum(kind, "The code relationship target kind is not supported.");
        CodeEvidenceValidation.Id(exactIdentityId, "A code relationship target requires an exact identity.");
        if (ownerId == Guid.Empty) throw new DomainException("A code relationship target owner cannot be empty.");
        if (revision is < 0) throw new DomainException("A code relationship target revision cannot be negative.");
        var display = CodeEvidenceValidation.Required(displaySnapshot, "A code relationship target display snapshot is required.");
        var stable = $"{kind}:{exactIdentityId:D}";
        return new(kind, exactIdentityId, ownerId, revision, stable, display);
    }
}

public sealed class GitLabMergeRequestRelationship
{
    private GitLabMergeRequestRelationship() { }

    public GitLabMergeRequestRelationship(Guid projectId, Guid releaseId, string instanceBaseUrl, long remoteProjectId, int mergeRequestIid,
        long? mergeRequestId, Guid? sourceSnapshotId, Guid? sourceSelectionEventId, string repositoryPathSnapshot,
        string mergeRequestUrlSnapshot, string mergeRequestTitleSnapshot, CodeRelationshipTarget target,
        CodeRelationshipMeaning meaning, string recordedBy, DateTimeOffset recordedAt)
    {
        CodeEvidenceValidation.Id(projectId, "A merge-request relationship requires a project.");
        CodeEvidenceValidation.Id(releaseId, "A merge-request relationship requires an implementation release.");
        if (sourceSnapshotId.HasValue != sourceSelectionEventId.HasValue)
            throw new DomainException("A merge-request source context must include both its snapshot and selection event.");
        if (remoteProjectId <= 0) throw new DomainException("A merge-request relationship requires a positive remote project identity.");
        if (mergeRequestIid <= 0) throw new DomainException("A merge-request relationship requires a positive merge-request IID.");
        if (mergeRequestId is <= 0) throw new DomainException("A merge-request relationship merge-request ID must be positive when present.");
        CodeEvidenceValidation.Enum(meaning, "The code relationship meaning is not supported.");
        ApplyCommon(projectId, releaseId, instanceBaseUrl, remoteProjectId, sourceSnapshotId, sourceSelectionEventId, repositoryPathSnapshot,
            target, meaning, recordedBy, recordedAt);
        Id = Guid.NewGuid(); MergeRequestIid = mergeRequestIid; MergeRequestId = mergeRequestId;
        MergeRequestUrlSnapshot = CodeEvidenceValidation.HttpsUrl(mergeRequestUrlSnapshot, "A merge-request URL snapshot is required.");
        MergeRequestTitleSnapshot = CodeEvidenceValidation.Required(mergeRequestTitleSnapshot, "A merge-request title snapshot is required.");
        RelationshipKind = CodeRelationshipKind.MergeRequest; IsActive = true; Version = 1; ActiveEdgeKey = BuildActiveEdgeKey();
    }

    private void ApplyCommon(Guid projectId, Guid releaseId, string instanceBaseUrl, long remoteProjectId, Guid? sourceSnapshotId,
        Guid? sourceSelectionEventId, string repositoryPathSnapshot, CodeRelationshipTarget target,
        CodeRelationshipMeaning meaning, string recordedBy, DateTimeOffset recordedAt)
    {
        CodeEvidenceValidation.Id(target.ExactIdentityId, "A code relationship requires an exact target identity.");
        CodeEvidenceValidation.Enum(target.Kind, "The code relationship target kind is not supported.");
        CodeEvidenceValidation.Id(projectId, "A code relationship requires a project.");
        CodeEvidenceValidation.Id(releaseId, "A code relationship requires an implementation release.");
        ProjectId = projectId; ReleaseId = releaseId;
        InstanceBaseUrl = CodeEvidenceValidation.HttpsUrl(instanceBaseUrl, "A code relationship requires its GitLab instance URL.");
        RemoteProjectId = remoteProjectId; SourceSnapshotId = sourceSnapshotId;
        SourceSelectionEventId = sourceSelectionEventId; RepositoryPathSnapshot = CodeEvidenceValidation.RepositoryPath(repositoryPathSnapshot, "A repository path snapshot is required.");
        TargetKind = target.Kind; TargetIdentityId = target.ExactIdentityId; TargetOwnerIdentityId = target.OwningIdentityId;
        TargetRevisionNumber = target.RevisionNumber; TargetStableIdentity = target.StableIdentity; TargetDisplaySnapshot = target.DisplaySnapshot;
        Meaning = meaning; RecordedBy = CodeEvidenceValidation.Actor(recordedBy); RecordedAt = recordedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public string InstanceBaseUrl { get; private set; } = string.Empty;
    public long RemoteProjectId { get; private set; }
    public int MergeRequestIid { get; private set; }
    public long? MergeRequestId { get; private set; }
    public Guid? SourceSnapshotId { get; private set; }
    public Guid? SourceSelectionEventId { get; private set; }
    public string RepositoryPathSnapshot { get; private set; } = string.Empty;
    public string MergeRequestUrlSnapshot { get; private set; } = string.Empty;
    public string MergeRequestTitleSnapshot { get; private set; } = string.Empty;
    public CodeRelationshipKind RelationshipKind { get; private set; }
    public CodeRelationshipTargetKind TargetKind { get; private set; }
    public Guid TargetIdentityId { get; private set; }
    public Guid? TargetOwnerIdentityId { get; private set; }
    public int? TargetRevisionNumber { get; private set; }
    public string TargetStableIdentity { get; private set; } = string.Empty;
    public string TargetDisplaySnapshot { get; private set; } = string.Empty;
    public CodeRelationshipMeaning Meaning { get; private set; }
    public bool IsActive { get; private set; }
    public string? ActiveEdgeKey { get; private set; }
    public string RecordedBy { get; private set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset? WithdrawnAt { get; private set; }
    public string? WithdrawnBy { get; private set; }
    public string? WithdrawalRationale { get; private set; }
    public string? ReAddedBy { get; private set; }
    public DateTimeOffset? ReAddedAt { get; private set; }

    public void Withdraw(long expectedVersion, string actor, string rationale, DateTimeOffset occurredAt)
    {
        if (!IsActive) throw new DomainException("An inactive merge-request relationship cannot be withdrawn twice.");
        if (expectedVersion < 1 || expectedVersion != Version) throw new DomainException("Another merge-request relationship change was saved. Refresh before retrying.");
        var validatedActor = CodeEvidenceValidation.Actor(actor);
        var validatedRationale = CodeEvidenceValidation.Required(rationale, "Withdrawing a code relationship requires a rationale.");
        IsActive = false; ActiveEdgeKey = null; WithdrawnBy = validatedActor;
        WithdrawalRationale = validatedRationale;
        WithdrawnAt = occurredAt; Version++;
    }

    public void ReAdd(long expectedVersion, string actor, DateTimeOffset occurredAt)
    {
        if (IsActive) throw new DomainException("An active merge-request relationship cannot be re-added.");
        if (expectedVersion < 1 || expectedVersion != Version) throw new DomainException("Another merge-request relationship change was saved. Refresh before retrying.");
        IsActive = true; ActiveEdgeKey = BuildActiveEdgeKey(); ReAddedBy = CodeEvidenceValidation.Actor(actor); ReAddedAt = occurredAt; Version++;
    }

    private string BuildActiveEdgeKey() =>
        $"{ProjectId:N}|{ReleaseId:N}|{InstanceBaseUrl.ToLowerInvariant()}|{RemoteProjectId}|mr:{MergeRequestIid}|{TargetKind}|{TargetIdentityId:N}|{Meaning}";
}

public sealed class GitLabFileRelationship
{
    private GitLabFileRelationship() { }

    public GitLabFileRelationship(Guid projectId, Guid releaseId, string instanceBaseUrl, long remoteProjectId, Guid sourceSnapshotId,
        Guid? sourceSelectionEventId, string commitSha, string path, int? startLine, int? endLine,
        int? mergeRequestIid, CodeRelationshipTarget target, CodeRelationshipMeaning meaning,
        string recordedBy, DateTimeOffset recordedAt)
    {
        CodeEvidenceValidation.Id(projectId, "A file relationship requires a project.");
        CodeEvidenceValidation.Id(releaseId, "A file relationship requires an implementation release.");
        CodeEvidenceValidation.Id(sourceSnapshotId, "A file relationship requires a source snapshot.");
        if (remoteProjectId <= 0) throw new DomainException("A file relationship requires a positive remote project identity.");
        if (mergeRequestIid is <= 0) throw new DomainException("A file relationship merge-request IID must be positive when present.");
        CodeEvidenceValidation.Lines(startLine, endLine); CodeEvidenceValidation.Enum(meaning, "The code relationship meaning is not supported.");
        CodeEvidenceValidation.Id(target.ExactIdentityId, "A code relationship requires an exact target identity.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId;
        InstanceBaseUrl = CodeEvidenceValidation.HttpsUrl(instanceBaseUrl, "A code relationship requires its GitLab instance URL.");
        RemoteProjectId = remoteProjectId;
        SourceSnapshotId = sourceSnapshotId; SourceSelectionEventId = sourceSelectionEventId;
        CommitSha = CodeEvidenceValidation.Sha(commitSha, "A file relationship requires its exact commit SHA.");
        Path = CodeEvidenceValidation.RelativeFilePath(path); StartLine = startLine; EndLine = endLine; MergeRequestIid = mergeRequestIid;
        RelationshipKind = CodeRelationshipKind.File; TargetKind = target.Kind; TargetIdentityId = target.ExactIdentityId;
        TargetOwnerIdentityId = target.OwningIdentityId; TargetRevisionNumber = target.RevisionNumber; TargetStableIdentity = target.StableIdentity;
        TargetDisplaySnapshot = target.DisplaySnapshot; Meaning = meaning; IsActive = true; Version = 1; ActiveEdgeKey = BuildActiveEdgeKey();
        RecordedBy = CodeEvidenceValidation.Actor(recordedBy); RecordedAt = recordedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public string InstanceBaseUrl { get; private set; } = string.Empty;
    public long RemoteProjectId { get; private set; }
    public Guid SourceSnapshotId { get; private set; }
    public Guid? SourceSelectionEventId { get; private set; }
    public string CommitSha { get; private set; } = string.Empty;
    public string Path { get; private set; } = string.Empty;
    public int? StartLine { get; private set; }
    public int? EndLine { get; private set; }
    public int? MergeRequestIid { get; private set; }
    public CodeRelationshipKind RelationshipKind { get; private set; }
    public CodeRelationshipTargetKind TargetKind { get; private set; }
    public Guid TargetIdentityId { get; private set; }
    public Guid? TargetOwnerIdentityId { get; private set; }
    public int? TargetRevisionNumber { get; private set; }
    public string TargetStableIdentity { get; private set; } = string.Empty;
    public string TargetDisplaySnapshot { get; private set; } = string.Empty;
    public CodeRelationshipMeaning Meaning { get; private set; }
    public bool IsActive { get; private set; }
    public string? ActiveEdgeKey { get; private set; }
    public string RecordedBy { get; private set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset? WithdrawnAt { get; private set; }
    public string? WithdrawnBy { get; private set; }
    public string? WithdrawalRationale { get; private set; }
    public string? ReAddedBy { get; private set; }
    public DateTimeOffset? ReAddedAt { get; private set; }

    public void Withdraw(long expectedVersion, string actor, string rationale, DateTimeOffset occurredAt)
    {
        if (!IsActive) throw new DomainException("An inactive file relationship cannot be withdrawn twice.");
        if (expectedVersion < 1 || expectedVersion != Version) throw new DomainException("Another file relationship change was saved. Refresh before retrying.");
        var validatedActor = CodeEvidenceValidation.Actor(actor);
        var validatedRationale = CodeEvidenceValidation.Required(rationale, "Withdrawing a code relationship requires a rationale.");
        IsActive = false; ActiveEdgeKey = null; WithdrawnBy = validatedActor;
        WithdrawalRationale = validatedRationale;
        WithdrawnAt = occurredAt; Version++;
    }

    public void ReAdd(long expectedVersion, string actor, DateTimeOffset occurredAt)
    {
        if (IsActive) throw new DomainException("An active file relationship cannot be re-added.");
        if (expectedVersion < 1 || expectedVersion != Version) throw new DomainException("Another file relationship change was saved. Refresh before retrying.");
        IsActive = true; ActiveEdgeKey = BuildActiveEdgeKey(); ReAddedBy = CodeEvidenceValidation.Actor(actor); ReAddedAt = occurredAt; Version++;
    }

    private string BuildActiveEdgeKey() =>
        $"{ProjectId:N}|{ReleaseId:N}|{InstanceBaseUrl.ToLowerInvariant()}|{RemoteProjectId}|file:{CommitSha}:{Path}:{StartLine?.ToString() ?? ""}-{EndLine?.ToString() ?? ""}|{TargetKind}|{TargetIdentityId:N}|{Meaning}";
}

public sealed class GitLabCodeRelationshipEvent
{
    private GitLabCodeRelationshipEvent() { }

    public GitLabCodeRelationshipEvent(CodeRelationshipKind relationshipKind, Guid relationshipId,
        CodeRelationshipEventKind eventKind, string actor, DateTimeOffset occurredAt, string? rationale = null)
    {
        CodeEvidenceValidation.Enum(relationshipKind, "The code relationship kind is not supported.");
        CodeEvidenceValidation.Enum(eventKind, "The code relationship event kind is not supported.");
        CodeEvidenceValidation.Id(relationshipId, "A code relationship event requires a relationship identity.");
        if (eventKind == CodeRelationshipEventKind.Withdrawn && string.IsNullOrWhiteSpace(rationale))
            throw new DomainException("Withdrawing a code relationship requires a rationale.");
        Id = Guid.NewGuid(); RelationshipKind = relationshipKind; RelationshipId = relationshipId; EventKind = eventKind;
        Actor = CodeEvidenceValidation.Actor(actor); OccurredAt = occurredAt; Rationale = rationale?.Trim();
    }

    public Guid Id { get; private set; }
    public CodeRelationshipKind RelationshipKind { get; private set; }
    public Guid RelationshipId { get; private set; }
    public CodeRelationshipEventKind EventKind { get; private set; }
    public string Actor { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public string? Rationale { get; private set; }
}

public sealed class CodeEvidenceDispositionSet
{
    private CodeEvidenceDispositionSet() { }

    public CodeEvidenceDispositionSet(Guid projectId, Guid releaseId, Guid requirementArtifactId,
        Guid requirementRevisionId, CodeEvidenceDisposition disposition, string? noCodeChangeRationale,
        Guid? sourceSelectionEventId, Guid? sourceSnapshotId, Guid? supersededLegacyRecordId,
        string recordedBy, DateTimeOffset recordedAt)
    {
        CodeEvidenceValidation.Id(projectId, "An evidence disposition requires a project.");
        CodeEvidenceValidation.Id(releaseId, "An evidence disposition requires a release.");
        CodeEvidenceValidation.Id(requirementArtifactId, "An evidence disposition requires a requirement artifact.");
        CodeEvidenceValidation.Id(requirementRevisionId, "An evidence disposition requires an exact requirement revision.");
        CodeEvidenceValidation.Enum(disposition, "The code evidence disposition is not supported.");
        if (disposition == CodeEvidenceDisposition.NoCodeChangeRequired)
        {
            if (string.IsNullOrWhiteSpace(noCodeChangeRationale)) throw new DomainException("A no-code disposition requires a rationale.");
            if (sourceSelectionEventId is not null || sourceSnapshotId is not null)
                throw new DomainException("A no-code disposition cannot carry a GitLab source selection.");
        }
        else
        {
            CodeEvidenceValidation.Id(sourceSelectionEventId ?? Guid.Empty, "A GitLab disposition requires its source selection event.");
            CodeEvidenceValidation.Id(sourceSnapshotId ?? Guid.Empty, "A GitLab disposition requires its source snapshot.");
        }
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId;
        RequirementArtifactId = requirementArtifactId; RequirementRevisionId = requirementRevisionId; Disposition = disposition;
        NoCodeChangeRationale = noCodeChangeRationale?.Trim() ?? string.Empty; SourceSelectionEventId = sourceSelectionEventId;
        SourceSnapshotId = sourceSnapshotId; SupersededLegacyRecordId = supersededLegacyRecordId;
        RecordedBy = CodeEvidenceValidation.Actor(recordedBy); RecordedAt = recordedAt;
    }

    public static CodeEvidenceDispositionSet CreateGitLab(Guid projectId, Guid releaseId, Guid requirementArtifactId,
        Guid requirementRevisionId, GitLabSourceSelectionEvent selectionEvent, GitLabSourceSnapshot sourceSnapshot,
        Guid? supersededLegacyRecordId, string recordedBy, DateTimeOffset recordedAt)
    {
        CodeEvidenceValidation.Id(selectionEvent.Id, "A GitLab evidence disposition requires a source selection event.");
        CodeEvidenceValidation.Id(sourceSnapshot.Id, "A GitLab evidence disposition requires a source snapshot.");
        if (selectionEvent.ProjectId != projectId || selectionEvent.ReleaseId != releaseId)
            throw new DomainException("The GitLab source selection belongs to a different project or release.");
        if (sourceSnapshot.ProjectId != projectId || selectionEvent.SourceSnapshotId != sourceSnapshot.Id)
            throw new DomainException("The GitLab source selection does not match its source snapshot.");
        return new(projectId, releaseId, requirementArtifactId, requirementRevisionId,
            CodeEvidenceDisposition.GitLabContributions, null, selectionEvent.Id, sourceSnapshot.Id,
            supersededLegacyRecordId, recordedBy, recordedAt);
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid RequirementArtifactId { get; private set; }
    public Guid RequirementRevisionId { get; private set; }
    public CodeEvidenceDisposition Disposition { get; private set; }
    public string NoCodeChangeRationale { get; private set; } = string.Empty;
    public Guid? SourceSelectionEventId { get; private set; }
    public Guid? SourceSnapshotId { get; private set; }
    /// <summary>Explicit legacy identity only; readers must never infer a fallback from this field.</summary>
    public Guid? SupersededLegacyRecordId { get; private set; }
    public string RecordedBy { get; private set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; private set; }
}

public sealed class CodeEvidenceContribution
{
    private CodeEvidenceContribution() { }

    public CodeEvidenceContribution(Guid evidenceSetId, Guid projectId, Guid releaseId,
        Guid requirementArtifactId, Guid requirementRevisionId, Guid sourceSnapshotId, CodeEvidenceContributionKind contributionKind, Guid? relationshipId,
        string instanceBaseUrl, long remoteProjectId, string repositoryPathSnapshot, int? mergeRequestIid, long? mergeRequestId,
        string? mergeRequestUrlSnapshot, string? mergeRequestTitleSnapshot, string? commitSha, string? filePath,
        int? startLine, int? endLine, CodeRelationshipTarget target, string recordedBy, DateTimeOffset recordedAt,
        string? mergeResultSha = null, GitLabMergeResultKind? mergeResultKind = null,
        DateTimeOffset? mergedAt = null, DateTimeOffset? providerObservedAt = null)
    {
        CodeEvidenceValidation.Id(evidenceSetId, "An evidence contribution requires an evidence set.");
        CodeEvidenceValidation.Id(projectId, "An evidence contribution requires a project.");
        CodeEvidenceValidation.Id(releaseId, "An evidence contribution requires a release.");
        CodeEvidenceValidation.Id(requirementArtifactId, "An evidence contribution requires a requirement artifact.");
        CodeEvidenceValidation.Id(requirementRevisionId, "An evidence contribution requires an exact requirement revision.");
        CodeEvidenceValidation.Id(sourceSnapshotId, "An evidence contribution requires its source snapshot.");
        CodeEvidenceValidation.Enum(contributionKind, "The evidence contribution kind is not supported.");
        if (remoteProjectId <= 0) throw new DomainException("An evidence contribution requires a positive remote project identity.");
        CodeEvidenceValidation.Lines(startLine, endLine);
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new DomainException("An evidence contribution requires the exact selected source commit.");
        CodeEvidenceValidation.Sha(commitSha, "An evidence contribution requires its exact source commit SHA.");
        if (contributionKind == CodeEvidenceContributionKind.MergeRequest)
        {
            if (mergeRequestIid is not > 0) throw new DomainException("A merge-request contribution requires a positive IID.");
            if (string.IsNullOrWhiteSpace(mergeRequestUrlSnapshot)) throw new DomainException("A merge-request contribution requires its URL snapshot.");
            MergeResultSha = CodeEvidenceValidation.Sha(mergeResultSha, "A merge-request contribution requires the observed exact merge or squash result SHA.");
            if (mergeResultKind is null || mergedAt is null || providerObservedAt is null)
                throw new DomainException("A merge-request contribution requires the result kind, merge time and provider observation time.");
            CodeEvidenceValidation.Enum(mergeResultKind.Value, "The merge result kind is not supported.");
            MergeResultKind = mergeResultKind; MergedAt = mergedAt; ProviderObservedAt = providerObservedAt;
        }
        else
        {
            if (mergeResultSha is not null || mergeResultKind is not null || mergedAt is not null || providerObservedAt is not null)
                throw new DomainException("A file contribution cannot carry merge-result facts.");
            if (string.IsNullOrWhiteSpace(filePath))
                throw new DomainException("A file contribution requires its exact path.");
            CodeEvidenceValidation.RelativeFilePath(filePath);
        }
        Id = Guid.NewGuid(); EvidenceSetId = evidenceSetId; ProjectId = projectId; ReleaseId = releaseId;
        RequirementArtifactId = requirementArtifactId; RequirementRevisionId = requirementRevisionId; SourceSnapshotId = sourceSnapshotId; ContributionKind = contributionKind; RelationshipId = relationshipId;
        InstanceBaseUrl = CodeEvidenceValidation.HttpsUrl(instanceBaseUrl, "An evidence contribution requires its GitLab instance URL.");
        RemoteProjectId = remoteProjectId; RepositoryPathSnapshot = CodeEvidenceValidation.RepositoryPath(repositoryPathSnapshot, "An evidence contribution requires a repository path snapshot.");
        MergeRequestIid = mergeRequestIid; MergeRequestId = mergeRequestId; MergeRequestUrlSnapshot = string.IsNullOrWhiteSpace(mergeRequestUrlSnapshot) ? null : CodeEvidenceValidation.HttpsUrl(mergeRequestUrlSnapshot, "A merge-request URL snapshot is invalid.");
        MergeRequestTitleSnapshot = string.IsNullOrWhiteSpace(mergeRequestTitleSnapshot) ? null : mergeRequestTitleSnapshot.Trim(); CommitSha = CodeEvidenceValidation.Sha(commitSha, "The contribution commit SHA is invalid.");
        FilePath = string.IsNullOrWhiteSpace(filePath) ? null : CodeEvidenceValidation.RelativeFilePath(filePath); StartLine = startLine; EndLine = endLine;
        TargetKind = target.Kind; TargetIdentityId = target.ExactIdentityId; TargetOwnerIdentityId = target.OwningIdentityId; TargetRevisionNumber = target.RevisionNumber;
        TargetStableIdentity = target.StableIdentity; TargetDisplaySnapshot = target.DisplaySnapshot; RecordedBy = CodeEvidenceValidation.Actor(recordedBy); RecordedAt = recordedAt;
    }

    public Guid Id { get; private set; }
    public Guid EvidenceSetId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid RequirementArtifactId { get; private set; }
    public Guid RequirementRevisionId { get; private set; }
    public Guid SourceSnapshotId { get; private set; }
    public CodeEvidenceContributionKind ContributionKind { get; private set; }
    /// <summary>Scalar only by design: an evidence contribution survives candidate relationship deletion.</summary>
    public Guid? RelationshipId { get; private set; }
    public string InstanceBaseUrl { get; private set; } = string.Empty;
    public long RemoteProjectId { get; private set; }
    public string RepositoryPathSnapshot { get; private set; } = string.Empty;
    public int? MergeRequestIid { get; private set; }
    public long? MergeRequestId { get; private set; }
    public string? MergeRequestUrlSnapshot { get; private set; }
    public string? MergeRequestTitleSnapshot { get; private set; }
    public string CommitSha { get; private set; } = string.Empty;
    public string? FilePath { get; private set; }
    /// <summary>The actual provider merge/squash result checked for ancestry; CommitSha remains the selected snapshot.</summary>
    public string? MergeResultSha { get; private set; }
    public GitLabMergeResultKind? MergeResultKind { get; private set; }
    public DateTimeOffset? MergedAt { get; private set; }
    public DateTimeOffset? ProviderObservedAt { get; private set; }
    public int? StartLine { get; private set; }
    public int? EndLine { get; private set; }
    public CodeRelationshipTargetKind TargetKind { get; private set; }
    public Guid TargetIdentityId { get; private set; }
    public Guid? TargetOwnerIdentityId { get; private set; }
    public int? TargetRevisionNumber { get; private set; }
    public string TargetStableIdentity { get; private set; } = string.Empty;
    public string TargetDisplaySnapshot { get; private set; } = string.Empty;
    public string RecordedBy { get; private set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; private set; }
}

public sealed class CodeEvidenceCurrentSelector
{
    private CodeEvidenceCurrentSelector() { }

    public CodeEvidenceCurrentSelector(Guid projectId, Guid releaseId, Guid requirementArtifactId, Guid requirementRevisionId, Guid evidenceSetId,
        string selectedBy, DateTimeOffset selectedAt)
    {
        CodeEvidenceValidation.Id(projectId, "An evidence selector requires a project.");
        CodeEvidenceValidation.Id(releaseId, "An evidence selector requires a release.");
        CodeEvidenceValidation.Id(requirementArtifactId, "An evidence selector requires a requirement artifact.");
        CodeEvidenceValidation.Id(requirementRevisionId, "An evidence selector requires an exact requirement revision.");
        CodeEvidenceValidation.Id(evidenceSetId, "An evidence selector requires an evidence set.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; RequirementArtifactId = requirementArtifactId; RequirementRevisionId = requirementRevisionId; EvidenceSetId = evidenceSetId; Version = 1;
        SelectedBy = CodeEvidenceValidation.Actor(selectedBy); SelectedAt = selectedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid RequirementArtifactId { get; private set; }
    public Guid RequirementRevisionId { get; private set; }
    public Guid EvidenceSetId { get; private set; }
    public long Version { get; private set; }
    public string SelectedBy { get; private set; } = string.Empty;
    public DateTimeOffset SelectedAt { get; private set; }

    public void Move(long expectedVersion, Guid evidenceSetId, string selectedBy, DateTimeOffset selectedAt)
    {
        if (expectedVersion < 1 || expectedVersion != Version)
            throw new DomainException("Another current evidence selection was saved. Refresh before changing it.");
        CodeEvidenceValidation.Id(evidenceSetId, "An evidence selector requires an evidence set.");
        EvidenceSetId = evidenceSetId; SelectedBy = CodeEvidenceValidation.Actor(selectedBy); SelectedAt = selectedAt; Version++;
    }
}

public sealed class CodeEvidenceInvalidation
{
    private CodeEvidenceInvalidation() { }

    public CodeEvidenceInvalidation(Guid evidenceSetId, Guid projectId, Guid releaseId, Guid requirementArtifactId,
        Guid requirementRevisionId, string invalidatedBy, string rationale, DateTimeOffset invalidatedAt)
    {
        CodeEvidenceValidation.Id(evidenceSetId, "An evidence invalidation requires an evidence set.");
        CodeEvidenceValidation.Id(projectId, "An evidence invalidation requires a project.");
        CodeEvidenceValidation.Id(releaseId, "An evidence invalidation requires a release.");
        CodeEvidenceValidation.Id(requirementArtifactId, "An evidence invalidation requires a requirement artifact.");
        CodeEvidenceValidation.Id(requirementRevisionId, "An evidence invalidation requires an exact requirement revision.");
        Id = Guid.NewGuid(); EvidenceSetId = evidenceSetId; ProjectId = projectId; ReleaseId = releaseId; RequirementArtifactId = requirementArtifactId; RequirementRevisionId = requirementRevisionId; InvalidatedBy = CodeEvidenceValidation.Actor(invalidatedBy);
        Rationale = CodeEvidenceValidation.Required(rationale, "Invalidating evidence requires a rationale."); InvalidatedAt = invalidatedAt;
    }

    public Guid Id { get; private set; }
    public Guid EvidenceSetId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid RequirementArtifactId { get; private set; }
    public Guid RequirementRevisionId { get; private set; }
    public string InvalidatedBy { get; private set; } = string.Empty;
    public string Rationale { get; private set; } = string.Empty;
    public DateTimeOffset InvalidatedAt { get; private set; }
}

/// <summary>Immutable identity of the frozen Code portion of one review-cycle manifest.</summary>
public sealed class CodeReviewCycleManifestIdentity
{
    private CodeReviewCycleManifestIdentity() { }

    public CodeReviewCycleManifestIdentity(Guid projectId, Guid releaseId, Guid releaseCampaignId, int approvalCycle,
        string format, int formatVersion, string manifestHash, Guid? sourceSelectionEventId, Guid? sourceSnapshotId,
        IReadOnlyCollection<Guid> evidenceReferenceIds, string frozenBy, DateTimeOffset frozenAt)
    {
        CodeEvidenceValidation.Id(projectId, "A Code manifest identity requires a project.");
        CodeEvidenceValidation.Id(releaseId, "A Code manifest identity requires a release.");
        CodeEvidenceValidation.Id(releaseCampaignId, "A Code manifest identity requires a release campaign.");
        if (approvalCycle < 1) throw new DomainException("A Code manifest approval cycle must be positive.");
        if (formatVersion < 1) throw new DomainException("A Code manifest format version must be positive.");
        if (sourceSelectionEventId.HasValue != sourceSnapshotId.HasValue)
            throw new DomainException("A Code manifest source selection must include both its event and snapshot.");
        if (formatVersion == 1 && (sourceSelectionEventId.HasValue || sourceSnapshotId.HasValue))
            throw new DomainException("A v1 Code manifest cannot carry v2 source identity references.");
        Id = Guid.NewGuid(); ProjectId = projectId; ReleaseId = releaseId; ReleaseCampaignId = releaseCampaignId; ApprovalCycle = approvalCycle;
        Format = CodeEvidenceValidation.Required(format, "A Code manifest format is required."); FormatVersion = formatVersion; ManifestHash = CodeEvidenceValidation.Sha256(manifestHash);
        SourceSelectionEventId = sourceSelectionEventId; SourceSnapshotId = sourceSnapshotId;
        EvidenceReferenceIdsJson = CodeEvidenceValidation.EvidenceIds(evidenceReferenceIds, formatVersion);
        FrozenBy = CodeEvidenceValidation.Actor(frozenBy); FrozenAt = frozenAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid ReleaseCampaignId { get; private set; }
    public int ApprovalCycle { get; private set; }
    public string Format { get; private set; } = string.Empty;
    public int FormatVersion { get; private set; }
    public string ManifestHash { get; private set; } = string.Empty;
    public Guid? SourceSelectionEventId { get; private set; }
    public Guid? SourceSnapshotId { get; private set; }
    public string EvidenceReferenceIdsJson { get; private set; } = string.Empty;
    public string FrozenBy { get; private set; } = string.Empty;
    public DateTimeOffset FrozenAt { get; private set; }
}
