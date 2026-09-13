using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;

namespace AeroLink.Domain.Tests;

public sealed class ProjectRepositoryConfigurationTests
{
    [Fact]
    public void Connect_now_is_unverified_until_server_probe_and_reconfiguration_clears_old_evidence()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new ProjectRepositoryConfiguration(Guid.NewGuid(), ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example.test/aerolink/rmt", "admin", now);
        Assert.Equal(ProjectRepositorySetupStatus.ConfiguredUnverified, config.Status);
        config.RecordVerification("admin", now.AddMinutes(1), 42, "aerolink/rmt");
        Assert.Equal(ProjectRepositorySetupStatus.Verified, config.Status);
        Assert.Equal(42, config.RemoteProjectId);
        Assert.Equal("aerolink/rmt", config.RemotePathWithNamespace);

        config.RecordVerificationFailure("admin", now.AddMinutes(2));
        Assert.Equal(ProjectRepositorySetupStatus.ConfiguredUnverified, config.Status);
        Assert.Null(config.RemoteProjectId);
        Assert.Null(config.RemotePathWithNamespace);
        Assert.Equal(now.AddMinutes(1), config.LastVerifiedAt);
        Assert.Equal("admin", config.LastVerificationFailureBy);

        config.Configure(config.Version, ProjectRepositorySetupMode.ConfigureLater, null, null, "admin", now.AddMinutes(3));
        Assert.Equal(ProjectRepositorySetupStatus.Pending, config.Status);
        Assert.Null(config.LastVerifiedAt);
        Assert.Null(config.RemoteProjectId);
        Assert.Null(config.RemotePathWithNamespace);
    }

    [Theory]
    [InlineData("http://gitlab.example.test/aerolink/rmt")]
    [InlineData("https://user:password@gitlab.example.test/rmt")]
    [InlineData("https://gitlab.example.test/rmt?token=secret")]
    [InlineData("https://gitlab.example.test/rmt#fragment")]
    public void Connect_now_rejects_unverifiable_endpoints(string endpoint)
        => Assert.Throws<DomainException>(() => new ProjectRepositoryConfiguration(Guid.NewGuid(),
            ProjectRepositorySetupMode.ConnectNow, "GitLab", endpoint, "admin", DateTimeOffset.UtcNow));
}
