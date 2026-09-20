using AeroLink.Domain.Common;

namespace AeroLink.Domain.Integrations;

/// <summary>
/// A present-day, synthetic source association for one explicitly authorized released build.
/// This is deliberately separate from the ordinary current source pointer and selection history:
/// it provides a read-only demonstration context without changing the historical release package.
/// </summary>
public sealed class ReleasedSyntheticSourceSupplement
{
    private ReleasedSyntheticSourceSupplement() { }

    public ReleasedSyntheticSourceSupplement(Guid projectId, Guid releaseId, Guid campaignId, Guid baselineId,
        Guid repositoryConfigurationId, long configurationVersion, Guid sourceSnapshotId,
        string instanceBaseUrl, long remoteProjectId, string repositoryPath, string commitSha,
        string requestedReference, string referenceKind, Guid operationId,
        int manifestVersion, string manifestDigest, int authorityScopeVersion, string authorityScopeDigest,
        string policyId, string authorizationReference,
        string reason, string recordedBy, DateTimeOffset recordedAt)
    {
        RequireId(projectId, "A released source supplement requires a project.");
        RequireId(releaseId, "A released source supplement requires a release.");
        RequireId(campaignId, "A released source supplement requires a release campaign.");
        RequireId(baselineId, "A released source supplement requires a baseline.");
        RequireId(repositoryConfigurationId, "A released source supplement requires a repository configuration.");
        RequireId(sourceSnapshotId, "A released source supplement requires a source snapshot.");
        RequireId(operationId, "A released source supplement requires an operation identity.");
        if (configurationVersion < 1) throw new DomainException("A released source supplement requires a positive configuration version.");
        var normalizedReferenceKind = Required(referenceKind, 30, "A released source supplement requires a supported reference kind.");
        if (manifestVersion != 1) throw new DomainException("The released source supplement manifest version is not supported.");
        if (authorityScopeVersion != 1) throw new DomainException("The released source supplement authority scope version is not supported.");

        Id = Guid.NewGuid();
        ProjectId = projectId;
        ReleaseId = releaseId;
        ReleaseCampaignId = campaignId;
        BaselineId = baselineId;
        RepositoryConfigurationId = repositoryConfigurationId;
        ConfigurationVersion = configurationVersion;
        SourceSnapshotId = sourceSnapshotId;
        InstanceBaseUrl = CodeEvidenceValidation.HttpsUrl(instanceBaseUrl,
            "A released source supplement requires its HTTPS GitLab origin.");
        if (remoteProjectId <= 0) throw new DomainException("A released source supplement requires a positive remote project identity.");
        RemoteProjectId = remoteProjectId;
        RepositoryPath = CodeEvidenceValidation.RepositoryPath(repositoryPath,
            "A released source supplement requires its repository path.");
        CommitSha = CodeEvidenceValidation.Sha(commitSha,
            "A released source supplement requires an exact commit SHA.");
        RequestedReference = Required(requestedReference, 256, "A released source supplement requires the observed reference.");
        OperationId = operationId;
        ManifestVersion = manifestVersion;
        AuthorityScopeVersion = authorityScopeVersion;
        AuthorityScopeDigest = RequiredHash(authorityScopeDigest, "A released source supplement requires its authority scope digest.");
        ManifestDigest = RequiredHash(manifestDigest, "A released source supplement requires its manifest digest.");
        PolicyId = Required(policyId, 120, "A released source supplement requires its policy identity.");
        AuthorizationReference = Required(authorizationReference, 300,
            "A released source supplement requires its owner authorization reference.");
        Reason = Required(reason, 4000, "A released source supplement requires an attributable reason.");
        RecordedBy = CodeEvidenceValidation.Actor(recordedBy);
        RecordedAt = recordedAt;
        ReferenceKind = normalizedReferenceKind;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid ReleaseCampaignId { get; private set; }
    public Guid BaselineId { get; private set; }
    public Guid RepositoryConfigurationId { get; private set; }
    public long ConfigurationVersion { get; private set; }
    public Guid SourceSnapshotId { get; private set; }
    public string InstanceBaseUrl { get; private set; } = string.Empty;
    public long RemoteProjectId { get; private set; }
    public string RepositoryPath { get; private set; } = string.Empty;
    public string CommitSha { get; private set; } = string.Empty;
    public string RequestedReference { get; private set; } = string.Empty;
    public string ReferenceKind { get; private set; } = string.Empty;
    public Guid OperationId { get; private set; }
    public int ManifestVersion { get; private set; }
    public int AuthorityScopeVersion { get; private set; }
    public string AuthorityScopeDigest { get; private set; } = string.Empty;
    public string ManifestDigest { get; private set; } = string.Empty;
    public string PolicyId { get; private set; } = string.Empty;
    public string AuthorizationReference { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public string RecordedBy { get; private set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; private set; }

    private static void RequireId(Guid value, string message)
    {
        if (value == Guid.Empty) throw new DomainException(message);
    }

    private static string Required(string value, int maxLength, string message)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength) throw new DomainException(message);
        return normalized;
    }

    private static string RequiredHash(string value, string message)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(c => !char.IsAsciiHexDigit(c))) throw new DomainException(message);
        return normalized.ToLowerInvariant();
    }
}
