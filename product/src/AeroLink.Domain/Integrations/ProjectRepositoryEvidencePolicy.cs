namespace AeroLink.Domain.Integrations;

public sealed record ProjectRepositoryEvidenceReadiness(string Status, bool CanRecordGitLabMerge, string Detail);
public sealed record ProjectRepositoryEvidenceRefusal(string Code, string Error);

/// <summary>Connection verification establishes repository identity, never that a supplied merge occurred.</summary>
public static class ProjectRepositoryEvidencePolicy
{
    public static ProjectRepositoryEvidenceReadiness Readiness(ProjectRepositoryConfiguration? configuration) => configuration switch
    {
        null => new("LegacyExistingProject", true, "This existing project has no project-creation repository setup record."),
        { Status: ProjectRepositorySetupStatus.Verified, RemoteProjectId: > 0, RemotePathWithNamespace: not null } =>
            new("Verified", true, "Repository identity is verified. Merge details remain attributable evidence supplied by the recorder."),
        { Status: ProjectRepositorySetupStatus.Pending } => new("Pending", false,
            "Repository setup is Pending. Configure and verify it before recording GitLab merge evidence. No-code decisions remain available."),
        _ => new("ConfiguredUnverified", false,
            "The configured repository is not verified. Verify it before recording GitLab merge evidence. No-code decisions remain available."),
    };

    public static ProjectRepositoryEvidenceRefusal? ValidateMerge(ProjectRepositoryConfiguration? configuration,
        string? repositoryPath, string? mergeRequestUrl, string? mergeRequestReference)
    {
        var readiness = Readiness(configuration);
        if (!readiness.CanRecordGitLabMerge)
            return new(readiness.Status == "Pending" ? "repository_pending" : "repository_unverified", readiness.Detail);
        if (configuration is null) return null; // Existing controlled projects retain their prior capture contract.
        if (!string.Equals(repositoryPath?.Trim(), configuration.RemotePathWithNamespace, StringComparison.Ordinal))
            return new("repository_identity_mismatch", "The mapping must use this project's verified repository path.");
        if (!Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out var endpoint)
            || !Uri.TryCreate(mergeRequestUrl?.Trim(), UriKind.Absolute, out var merge)
            || merge.Scheme != endpoint.Scheme || merge.IdnHost != endpoint.IdnHost || merge.Port != endpoint.Port
            || merge.UserInfo.Length != 0 || merge.Query.Length != 0 || merge.Fragment.Length != 0)
            return new("repository_identity_mismatch", "The merge request must belong to this project's verified GitLab repository.");
        var projectPath = Uri.UnescapeDataString(endpoint.AbsolutePath).TrimEnd('/');
        if (projectPath.EndsWith(".git", StringComparison.Ordinal)) projectPath = projectPath[..^4];
        var prefix = projectPath + "/-/merge_requests/";
        var mergePath = Uri.UnescapeDataString(merge.AbsolutePath).TrimEnd('/');
        var number = mergePath.StartsWith(prefix, StringComparison.Ordinal) ? mergePath[prefix.Length..] : "";
        if (number.Length == 0 || number.Any(c => !char.IsAsciiDigit(c)) || !long.TryParse(number, out var iid) || iid <= 0
            || !string.Equals(mergeRequestReference?.Trim(), "!" + number, StringComparison.Ordinal))
            return new("repository_identity_mismatch", "Use a merge request URL and matching !number from this project's verified repository.");
        return null;
    }
}
