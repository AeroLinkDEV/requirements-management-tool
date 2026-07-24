using AeroLink.Domain.Identity;

namespace AeroLink.Infrastructure.Tests;

public sealed class ExternalIdentityMappingTests
{
    [Fact]
    public void Provider_configuration_is_canonical_and_can_be_disabled()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new ExternalIdentityProvider(
            " Corporate-Entra ",
            "Corporate Entra ID",
            ExternalIdentityProtocol.OpenIdConnect,
            "https://login.example.test/tenant/v2.0",
            "sub",
            "groups",
            "system.admin",
            now);

        Assert.Equal("corporate-entra", provider.Key);
        Assert.True(provider.Enabled);
        Assert.Equal(ExternalIdentityProtocol.OpenIdConnect, provider.Protocol);

        provider.Disable(now.AddMinutes(1));
        Assert.False(provider.Enabled);
        Assert.Equal(now.AddMinutes(1), provider.DisabledAt);

        provider.Enable();
        Assert.True(provider.Enabled);
        Assert.Null(provider.DisabledAt);
    }

    [Fact]
    public void Group_mapping_is_provider_and_program_scoped_and_matches_case_insensitively()
    {
        var providerId = Guid.NewGuid();
        var programId = Guid.NewGuid();
        var mapping = new ExternalGroupRoleMapping(
            providerId,
            " AeroLink-FMS-Approvers ",
            programId,
            ProgramRole.Approver,
            "system.admin",
            DateTimeOffset.UtcNow);

        Assert.Equal("aerolink-fms-approvers", mapping.ExternalGroup);
        Assert.True(mapping.Matches(providerId, "AEROLINK-FMS-APPROVERS"));
        Assert.False(mapping.Matches(Guid.NewGuid(), "AEROLINK-FMS-APPROVERS"));
        Assert.Equal(programId, mapping.ProgramId);
        Assert.Equal(ProgramRole.Approver, mapping.Role);
    }

    [Fact]
    public void Disabled_group_mapping_never_grants_authority()
    {
        var providerId = Guid.NewGuid();
        var mapping = new ExternalGroupRoleMapping(
            providerId,
            "aerolink-reviewers",
            Guid.NewGuid(),
            ProgramRole.Reviewer,
            "system.admin",
            DateTimeOffset.UtcNow);

        mapping.Disable(DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.False(mapping.Matches(providerId, "aerolink-reviewers"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Provider_rejects_missing_trust_anchor(string issuer)
    {
        Assert.Throws<ArgumentException>(() => new ExternalIdentityProvider(
            "provider",
            "Provider",
            ExternalIdentityProtocol.Saml2,
            issuer,
            "nameid",
            "groups",
            "system.admin",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Mapping_rejects_unscoped_provider_or_program()
    {
        Assert.Throws<ArgumentException>(() => new ExternalGroupRoleMapping(
            Guid.Empty,
            "group",
            Guid.NewGuid(),
            ProgramRole.Engineer,
            "system.admin",
            DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentException>(() => new ExternalGroupRoleMapping(
            Guid.NewGuid(),
            "group",
            Guid.Empty,
            ProgramRole.Engineer,
            "system.admin",
            DateTimeOffset.UtcNow));
    }
}