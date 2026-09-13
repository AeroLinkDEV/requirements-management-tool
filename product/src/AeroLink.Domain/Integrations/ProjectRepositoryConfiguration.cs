using AeroLink.Domain.Common;

namespace AeroLink.Domain.Integrations;

public enum ProjectRepositorySetupMode { ConfigureLater, ConnectNow }
public enum ProjectRepositorySetupStatus { Pending, ConfiguredUnverified, Verified }

/// <summary>
/// Project-scoped repository setup retained independently of the recoverable wizard. A URL or provider name
/// is configuration evidence only; the service adapter must perform a supported probe before Verified is valid.
/// </summary>
public sealed class ProjectRepositoryConfiguration
{
    private ProjectRepositoryConfiguration() { }

    public ProjectRepositoryConfiguration(Guid projectId, ProjectRepositorySetupMode mode, string? provider,
        string? endpoint, string actor, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("Repository setup requires a project.");
        if (!Enum.IsDefined(mode)) throw new DomainException("Repository setup mode is not supported.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("Repository setup requires an attributable actor.");
        if (mode == ProjectRepositorySetupMode.ConnectNow)
            ValidateEndpoint(endpoint);
        Id = Guid.NewGuid(); ProjectId = projectId; Mode = mode;
        Status = mode == ProjectRepositorySetupMode.ConfigureLater
            ? ProjectRepositorySetupStatus.Pending : ProjectRepositorySetupStatus.ConfiguredUnverified;
        Provider = provider?.Trim(); Endpoint = endpoint?.Trim();
        ConfiguredBy = actor.Trim(); ConfiguredAt = now; Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public ProjectRepositorySetupMode Mode { get; private set; }
    public ProjectRepositorySetupStatus Status { get; private set; }
    public string? Provider { get; private set; }
    public string? Endpoint { get; private set; }
    public string ConfiguredBy { get; private set; } = string.Empty;
    public DateTimeOffset ConfiguredAt { get; private set; }
    public DateTimeOffset? LastVerifiedAt { get; private set; }
    public string? LastVerifiedBy { get; private set; }
    /// <summary>Server-observed provider identity; never accepted from browser status claims.</summary>
    public long? RemoteProjectId { get; private set; }
    public string? RemotePathWithNamespace { get; private set; }
    public DateTimeOffset? LastVerificationFailureAt { get; private set; }
    public string? LastVerificationFailureBy { get; private set; }
    public long Version { get; private set; }

    /// <summary>Changes the project-scoped connection while retaining a concurrency token and clearing stale verification.</summary>
    public void Configure(long expectedVersion, ProjectRepositorySetupMode mode, string? provider,
        string? endpoint, string actor, DateTimeOffset now)
    {
        if (expectedVersion < 1 || expectedVersion != Version)
            throw new DomainException("Another repository configuration was saved. Refresh before editing it.");
        if (!Enum.IsDefined(mode)) throw new DomainException("Repository setup mode is not supported.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("Repository setup requires an attributable actor.");
        if (mode == ProjectRepositorySetupMode.ConfigureLater)
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
                throw new DomainException("A deferred repository cannot carry an endpoint.");
        }
        else
            ValidateEndpoint(endpoint);

        Mode = mode;
        Status = mode == ProjectRepositorySetupMode.ConfigureLater
            ? ProjectRepositorySetupStatus.Pending : ProjectRepositorySetupStatus.ConfiguredUnverified;
        Provider = provider?.Trim(); Endpoint = endpoint?.Trim();
        ConfiguredBy = actor.Trim(); ConfiguredAt = now;
        LastVerifiedAt = null; LastVerifiedBy = null; RemoteProjectId = null; RemotePathWithNamespace = null;
        LastVerificationFailureAt = null; LastVerificationFailureBy = null; Version++;
    }

    public void RecordVerification(string actor, DateTimeOffset now, long remoteProjectId, string remotePathWithNamespace)
    {
        if (Mode != ProjectRepositorySetupMode.ConnectNow)
            throw new DomainException("A deferred repository must be configured before verification.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("Repository verification requires an actor.");
        if (remoteProjectId <= 0) throw new DomainException("Repository verification requires a positive remote project identity.");
        if (string.IsNullOrWhiteSpace(remotePathWithNamespace))
            throw new DomainException("Repository verification requires the provider namespace path.");
        Status = ProjectRepositorySetupStatus.Verified; LastVerifiedAt = now; LastVerifiedBy = actor.Trim();
        RemoteProjectId = remoteProjectId; RemotePathWithNamespace = remotePathWithNamespace.Trim();
        LastVerificationFailureAt = null; LastVerificationFailureBy = null; Version++;
    }

    /// <summary>Clears current verified status after a server probe fails while retaining the last success audit.</summary>
    public void RecordVerificationFailure(string actor, DateTimeOffset now)
    {
        if (Mode != ProjectRepositorySetupMode.ConnectNow)
            throw new DomainException("A deferred repository must be configured before verification.");
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("Repository verification requires an actor.");
        Status = ProjectRepositorySetupStatus.ConfiguredUnverified;
        RemoteProjectId = null; RemotePathWithNamespace = null;
        LastVerificationFailureAt = now; LastVerificationFailureBy = actor.Trim(); Version++;
    }

    private static void ValidateEndpoint(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new DomainException("A connected repository requires an HTTPS endpoint without credentials, query, or fragment.");
    }
}
