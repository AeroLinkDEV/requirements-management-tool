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
            "https://login.example.test/tenant/v2.0/",
            "sub",
            "groups",
            "system.admin",
            now);

        Assert.Equal("corporate-entra", provider.Key);
        Assert.Equal("https://login.example.test/tenant/v2.0", provider.Issuer);
        Assert.True(provider.Enabled);
        Assert.Equal(ExternalIdentityProtocol.OpenIdConnect, provider.Protocol);

        // Scheme, host and default port are compared per RFC 3986; the path is not, because a
        // case-folded path would widen the trust anchor beyond the issuer that was configured.
        Assert.True(provider.MatchesIssuer("HTTPS://LOGIN.EXAMPLE.TEST/tenant/v2.0/"));
        Assert.True(provider.MatchesIssuer("https://login.example.test:443/tenant/v2.0"));
        Assert.False(provider.MatchesIssuer("HTTPS://LOGIN.EXAMPLE.TEST/TENANT/V2.0/"));
        Assert.False(provider.MatchesIssuer("https://login.example.test/tenant"));
        Assert.False(provider.MatchesIssuer("https://login.example.test.attacker.example/tenant/v2.0"));

        provider.Disable(now.AddMinutes(1));
        Assert.False(provider.Enabled);
        Assert.False(provider.MatchesIssuer(provider.Issuer));
        Assert.Equal(now.AddMinutes(1), provider.DisabledAt);

        provider.Enable();
        Assert.True(provider.Enabled);
        Assert.Null(provider.DisabledAt);
    }

    [Fact]
    public void Provider_rejects_invalid_protocol_or_non_absolute_issuer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExternalIdentityProvider(
            "provider",
            "Provider",
            (ExternalIdentityProtocol)999,
            "https://identity.example.test",
            "sub",
            "groups",
            "system.admin",
            DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentException>(() => new ExternalIdentityProvider(
            "provider",
            "Provider",
            ExternalIdentityProtocol.Saml2,
            "identity.example.test",
            "nameid",
            "groups",
            "system.admin",
            DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("https://identity.example.test/tenant?probe=1")]
    [InlineData("https://identity.example.test/tenant#fragment")]
    [InlineData("ftp://identity.example.test/tenant")]
    public void Provider_rejects_a_trust_anchor_that_is_not_a_bare_http_identifier(string issuer)
    {
        Assert.Throws<ArgumentException>(() => new ExternalIdentityProvider(
            "provider",
            "Provider",
            ExternalIdentityProtocol.OpenIdConnect,
            issuer,
            "sub",
            "groups",
            "system.admin",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Provider_and_mapping_reject_input_beyond_the_stored_column_bounds()
    {
        Assert.Throws<ArgumentException>(() => new ExternalIdentityProvider(
            new string('k', ExternalIdentityProvider.KeyMaxLength + 1),
            "Provider",
            ExternalIdentityProtocol.OpenIdConnect,
            "https://identity.example.test",
            "sub",
            "groups",
            "system.admin",
            DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentException>(() => new ExternalIdentityProvider(
            "provider",
            new string('d', ExternalIdentityProvider.DisplayNameMaxLength + 1),
            ExternalIdentityProtocol.OpenIdConnect,
            "https://identity.example.test",
            "sub",
            "groups",
            "system.admin",
            DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentException>(() => new ExternalGroupRoleMapping(
            Guid.NewGuid(),
            new string('g', ExternalGroupRoleMapping.ExternalGroupMaxLength + 1),
            Guid.NewGuid(),
            ProgramRole.Engineer,
            "system.admin",
            DateTimeOffset.UtcNow));
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
    public void Mapping_match_fails_closed_for_malformed_external_input()
    {
        var mapping = new ExternalGroupRoleMapping(
            Guid.NewGuid(),
            "aerolink-reviewers",
            Guid.NewGuid(),
            ProgramRole.Reviewer,
            "system.admin",
            DateTimeOffset.UtcNow);

        Assert.False(mapping.Matches(Guid.Empty, "aerolink-reviewers"));
        Assert.False(mapping.Matches(mapping.ProviderId, null));
        Assert.False(mapping.Matches(mapping.ProviderId, ""));
        Assert.False(mapping.Matches(mapping.ProviderId, "   "));
    }

    [Fact]
    public void Disabled_group_mapping_never_grants_authority()
    {
        var now = DateTimeOffset.UtcNow;
        var providerId = Guid.NewGuid();
        var mapping = new ExternalGroupRoleMapping(
            providerId,
            "aerolink-reviewers",
            Guid.NewGuid(),
            ProgramRole.Reviewer,
            "system.admin",
            now);

        mapping.Disable(now.AddMinutes(1));
        mapping.Disable(now.AddMinutes(2));

        Assert.False(mapping.Matches(providerId, "aerolink-reviewers"));
        Assert.Equal(now.AddMinutes(1), mapping.DisabledAt);
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
    public void Mapping_rejects_unscoped_provider_program_or_unknown_role()
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

        Assert.Throws<ArgumentOutOfRangeException>(() => new ExternalGroupRoleMapping(
            Guid.NewGuid(),
            "group",
            Guid.NewGuid(),
            (ProgramRole)999,
            "system.admin",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Lifecycle_timestamps_cannot_precede_creation()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new ExternalIdentityProvider(
            "provider",
            "Provider",
            ExternalIdentityProtocol.OpenIdConnect,
            "https://identity.example.test",
            "sub",
            "groups",
            "system.admin",
            now);
        var mapping = new ExternalGroupRoleMapping(
            provider.Id,
            "group",
            Guid.NewGuid(),
            ProgramRole.Engineer,
            "system.admin",
            now);

        Assert.Throws<ArgumentOutOfRangeException>(() => provider.Disable(now.AddSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Disable(now.AddSeconds(-1)));
    }
}
