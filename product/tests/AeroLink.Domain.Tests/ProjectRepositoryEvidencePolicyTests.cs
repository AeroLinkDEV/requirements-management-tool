using AeroLink.Domain.Integrations;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class ProjectRepositoryEvidencePolicyTests
{
    [Theory]
    [InlineData("foreign/repo", "https://code.example.test/hosting/company/software/-/merge_requests/12", "!12")]
    [InlineData("company/software", "https://gitlab.attacker.test/hosting/company/software/-/merge_requests/12", "!12")]
    [InlineData("company/software", "https://code.example.test/hosting/foreign/software/-/merge_requests/12", "!12")]
    [InlineData("company/software", "https://code.example.test/hosting/company/software/-/merge_requests/12?redirect=elsewhere", "!12")]
    [InlineData("company/software", "https://code.example.test/hosting/company/software/-/merge_requests/12", "!13")]
    [InlineData("company/software", "https://user@code.example.test/hosting/company/software/-/merge_requests/12", "!12")]
    public void Foreign_or_ambiguous_merge_identity_is_refused(string path, string url, string reference)
    {
        Assert.Equal("repository_identity_mismatch", ProjectRepositoryEvidencePolicy.ValidateMerge(Verified(), path, url, reference)?.Code);
    }

    [Fact]
    public void Verified_project_supports_a_GitLab_installation_whose_host_does_not_contain_gitlab()
    {
        var configuration = Verified();
        var record = new CodeTraceabilityRecord(configuration.ProjectId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            CodeTraceDisposition.GitLabMerge, "company/software", "!12", "Implement operating-mode storage",
            "https://code.example.test/hosting/company/software/-/merge_requests/12", new string('a', 40),
            DateTimeOffset.UtcNow, "", false, "engineer", DateTimeOffset.UtcNow, configuration);
        Assert.Equal("company/software", record.RepositoryPath);
        var verifiedAt = configuration.LastVerifiedAt;
        var version = configuration.Version;
        configuration.RecordVerificationFailure("admin", DateTimeOffset.UtcNow);
        Assert.Equal("repository_unverified", ProjectRepositoryEvidencePolicy.ValidateMerge(configuration, record.RepositoryPath,
            record.MergeRequestUrl, record.MergeRequestReference)?.Code);
        configuration.Configure(configuration.Version, ProjectRepositorySetupMode.ConnectNow, "GitLab",
            "https://code.example.test/hosting/company/replacement", "admin", DateTimeOffset.UtcNow);
        configuration.RecordVerification("other.admin", DateTimeOffset.UtcNow, 91, "company/replacement");
        Assert.Equal(72, record.VerifiedRemoteProjectId);
        Assert.Equal("https://code.example.test/hosting/company/software.git", record.VerifiedRepositoryEndpoint);
        Assert.Equal("company/software", record.VerifiedRepositoryPath);
        Assert.Equal(version, record.RepositoryConfigurationVersion);
        Assert.Equal(verifiedAt, record.RepositoryVerifiedAt);
        Assert.Equal("admin", record.RepositoryVerifiedBy);
    }

    [Fact]
    public void Pending_is_a_prerequisite_and_does_not_masquerade_as_verified_legacy_configuration()
    {
        var pending = new ProjectRepositoryConfiguration(Guid.NewGuid(), ProjectRepositorySetupMode.ConfigureLater,
            null, null, "admin", DateTimeOffset.UtcNow);
        Assert.False(ProjectRepositoryEvidencePolicy.Readiness(pending).CanRecordGitLabMerge);
        Assert.Equal("repository_pending", ProjectRepositoryEvidencePolicy.ValidateMerge(pending, "company/software", "https://gitlab.example.test/x", "!1")?.Code);
        Assert.Equal("LegacyExistingProject", ProjectRepositoryEvidencePolicy.Readiness(null).Status);
        Assert.Null(ProjectRepositoryEvidencePolicy.ValidateMerge(null, "company/software", "https://gitlab.example.test/x", "!1"));
    }

    private static ProjectRepositoryConfiguration Verified()
    {
        var configuration = new ProjectRepositoryConfiguration(Guid.NewGuid(), ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://code.example.test/hosting/company/software.git", "admin", DateTimeOffset.UtcNow);
        configuration.RecordVerification("admin", DateTimeOffset.UtcNow, 72, "company/software");
        return configuration;
    }
}
