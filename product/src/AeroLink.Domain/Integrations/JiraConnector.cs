using AeroLink.Domain.Common;

namespace AeroLink.Domain.Integrations;

public enum JiraLinkState { Pending, Linked, Failed }

/// <summary>
/// A project's connection to one Jira instance.
///
/// Change requests are where engineering decides what will change; Jira is usually where the programme
/// tracks that the work happened. Without a link between them somebody retypes the same change into both,
/// and the two drift until nobody trusts either.
///
/// AeroLink pushes and reads; it does not become the tracker. Jira holds the work item and its status;
/// AeroLink holds the controlled record and its approvals. Each stays authoritative for what it is actually
/// for, which is why this is a link rather than a sync — a bidirectional sync would need one of them to win
/// a conflict, and neither should.
/// </summary>
public sealed class JiraConnection
{
    private JiraConnection() { }

    public JiraConnection(Guid projectId, string baseUrl, string projectKey, string issueType,
        string userName, string protectedApiToken, string actor, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A Jira connection belongs to a project.");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new DomainException("The Jira base address must be an absolute HTTP or HTTPS URL.");
        if (string.IsNullOrWhiteSpace(projectKey)) throw new DomainException("A Jira project key is required.");
        if (string.IsNullOrWhiteSpace(issueType)) throw new DomainException("A Jira issue type is required.");
        if (string.IsNullOrWhiteSpace(protectedApiToken)) throw new DomainException("Jira credentials are required.");

        Id = Guid.NewGuid();
        ProjectId = projectId;
        BaseUrl = uri.ToString().TrimEnd('/');
        // Jira rejects a lowercase key, and a connection that fails on first use is worse than one that
        // refuses to be created.
        ProjectKey = projectKey.Trim().ToUpperInvariant();
        IssueType = issueType.Trim();
        UserName = userName.Trim();
        ProtectedApiToken = protectedApiToken;
        IsEnabled = true;
        CreatedBy = actor.Trim();
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string BaseUrl { get; private set; } = "";
    public string ProjectKey { get; private set; } = "";
    public string IssueType { get; private set; } = "";
    /// <summary>Blank for a Data Center instance using a bearer personal access token.</summary>
    public string UserName { get; private set; } = "";
    /// <summary>
    /// Encrypted at rest by the data-protection provider, the same as webhook signing secrets. It is never
    /// returned by any endpoint; a caller can replace it but never read it back.
    /// </summary>
    public string ProtectedApiToken { get; private set; } = "";
    public bool IsEnabled { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? LastVerifiedAt { get; private set; }
    public string? LastError { get; private set; }

    public void SetEnabled(bool enabled, DateTimeOffset now) { IsEnabled = enabled; UpdatedAt = now; }

    public void Reconfigure(string projectKey, string issueType, string userName, string? protectedApiToken, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) throw new DomainException("A Jira project key is required.");
        if (string.IsNullOrWhiteSpace(issueType)) throw new DomainException("A Jira issue type is required.");
        ProjectKey = projectKey.Trim().ToUpperInvariant();
        IssueType = issueType.Trim();
        UserName = userName.Trim();
        // Omitting the token keeps the stored one. Requiring it on every edit would make people paste
        // credentials to change an issue type, which is how credentials end up in chat messages.
        if (!string.IsNullOrWhiteSpace(protectedApiToken)) ProtectedApiToken = protectedApiToken;
        UpdatedAt = now;
    }

    /// <summary>Records the outcome of a reachability check, so a broken connection is visible before it is needed.</summary>
    public void RecordVerification(bool succeeded, string? error, DateTimeOffset now)
    {
        LastError = succeeded ? null : Trim(error);
        if (succeeded) LastVerifiedAt = now;
        UpdatedAt = now;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > 1000 ? value[..1000] : value;
}

/// <summary>
/// The link between one AeroLink artifact and one Jira issue.
///
/// The link is a record in its own right rather than a column on the change request, because it has its own
/// lifecycle: it can fail, be retried, and carry a status that changes without the change request changing.
/// Putting a Jira key on the aggregate would also mean a tracker being unreachable could block an approval,
/// which is the wrong order of authority entirely.
/// </summary>
public sealed class JiraIssueLink
{
    private JiraIssueLink() { }

    public JiraIssueLink(Guid projectId, Guid connectionId, string artifactType, Guid artifactId,
        string artifactNumber, string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(artifactType)) throw new DomainException("A Jira link needs the kind of artifact it links.");
        if (artifactId == Guid.Empty) throw new DomainException("A Jira link needs an artifact.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        ConnectionId = connectionId;
        ArtifactType = artifactType.Trim();
        ArtifactId = artifactId;
        ArtifactNumber = artifactNumber.Trim();
        State = JiraLinkState.Pending;
        CreatedBy = actor.Trim();
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string ArtifactType { get; private set; } = "";
    public Guid ArtifactId { get; private set; }
    public string ArtifactNumber { get; private set; } = "";
    public string IssueKey { get; private set; } = "";
    public string IssueUrl { get; private set; } = "";
    /// <summary>Jira's own status name, reflected as read. Mapping it to an AeroLink state would be inventing
    /// a correspondence that no two Jira projects agree on.</summary>
    public string IssueStatus { get; private set; } = "";
    public JiraLinkState State { get; private set; }
    public string? LastError { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? StatusReadAt { get; private set; }

    public void RecordIssue(string issueKey, string issueUrl, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(issueKey)) throw new DomainException("A created Jira issue must have a key.");
        IssueKey = issueKey.Trim();
        IssueUrl = issueUrl.Trim();
        State = JiraLinkState.Linked;
        LastError = null;
        UpdatedAt = now;
    }

    public void RecordStatus(string status, DateTimeOffset now)
    {
        IssueStatus = string.IsNullOrWhiteSpace(status) ? "" : status.Trim();
        StatusReadAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Records that the tracker could not be reached or refused the request. The link is kept, not deleted,
    /// so somebody can see that a push was attempted and why it did not land.
    /// </summary>
    public void RecordFailure(string error, DateTimeOffset now)
    {
        State = JiraLinkState.Failed;
        LastError = string.IsNullOrWhiteSpace(error) ? "The tracker refused the request." : error.Length > 1000 ? error[..1000] : error;
        UpdatedAt = now;
    }

    public void Retry(DateTimeOffset now)
    {
        if (State == JiraLinkState.Linked) throw new DomainException("This artifact is already linked to a Jira issue.");
        State = JiraLinkState.Pending;
        LastError = null;
        UpdatedAt = now;
    }
}
