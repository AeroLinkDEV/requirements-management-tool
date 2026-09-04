using Microsoft.Extensions.Configuration;

namespace AeroLink.Api.Tests;

/// <summary>
/// What the runtime identity surface says, and — more importantly — what it must never say.
///
/// This endpoint exists so a launcher can tell a matching process from a stale one, and so an operator can
/// tell HOME CANONICAL from WORK-LAPTOP LOCAL at a glance. It is anonymous by design, sitting under the
/// `/health` prefix that Program.cs already lets through unauthenticated, which is exactly why the shape of
/// what it publishes is a contract rather than a convenience: anything it carries is readable by anyone who
/// can reach the port.
/// </summary>
public sealed class RuntimeIdentityTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value))).Build();

    [Fact]
    public void The_launcher_declared_source_mode_and_instance_are_reported_exactly()
    {
        var identity = RuntimeIdentityEndpoints.Resolve(Configuration(
            ("Runtime:SourceSha", "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"),
            ("Runtime:SourceIdentity", "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"),
            ("Runtime:Mode", "HOME-PRODUCTION"),
            ("Instance:Label", "HOME CANONICAL"),
            ("Instance:Classification", "HomeCanonical"),
            ("ConnectionStrings:AeroLink", "Host=127.0.0.1;Port=54329;Database=aerolink;Username=postgres;Password=hunter2")));

        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", identity.SourceSha);
        Assert.Equal("a1b2c3d4", identity.SourceShortSha);
        Assert.Equal("HOME-PRODUCTION", identity.Mode);
        Assert.Equal("HOME CANONICAL", identity.InstanceLabel);
        Assert.Equal("HomeCanonical", identity.InstanceClassification);
    }

    /// <summary>
    /// The database is identity — which installation is this? — and the rest of the connection string is
    /// topology and credentials. Only the name survives, and it is not filtered downstream: it is never read.
    /// </summary>
    [Fact]
    public void Only_the_database_name_is_taken_from_the_connection_string()
    {
        var identity = RuntimeIdentityEndpoints.Resolve(Configuration(
            ("ConnectionStrings:AeroLink", "Host=192.0.2.10;Port=54329;Database=aerolink;Username=postgres;Password=hunter2")));

        Assert.Equal("aerolink", identity.DatabaseName);
        var published = System.Text.Json.JsonSerializer.Serialize(identity);
        Assert.DoesNotContain("hunter2", published);
        Assert.DoesNotContain("postgres", published);
        Assert.DoesNotContain("192.0.2.10", published);
        Assert.DoesNotContain("54329", published);
    }

    /// <summary>
    /// A process started outside the supported launchers declares nothing, and must not be given a plausible
    /// identity. "unknown" can never equal the identity a launcher expects, so such a process is treated as
    /// stale and restarted rather than silently reused — which is the safe direction to be wrong in.
    /// </summary>
    [Fact]
    public void An_undeclared_runtime_reports_unknown_rather_than_a_guess()
    {
        var identity = RuntimeIdentityEndpoints.Resolve(Configuration());

        Assert.Equal("unknown", identity.SourceSha);
        Assert.Equal("unknown", identity.SourceIdentity);
        Assert.Equal("UNKNOWN", identity.Mode);
        Assert.Equal("Undeclared", identity.InstanceClassification);
        Assert.Null(identity.DatabaseName);
    }

    /// <summary>
    /// For a dirty development checkout the launcher's identity carries a worktree fingerprint the commit SHA
    /// cannot. The runtime must publish that, not the SHA underneath it, or an edited tree would look
    /// identical to the commit it came from.
    /// </summary>
    [Fact]
    public void A_worktree_identity_is_published_in_preference_to_the_bare_commit_sha()
    {
        var identity = RuntimeIdentityEndpoints.Resolve(Configuration(
            ("Runtime:SourceSha", "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"),
            ("Runtime:SourceIdentity", "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678+worktree:0123456789abcdef")));

        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678+worktree:0123456789abcdef", identity.SourceIdentity);
        Assert.NotEqual(identity.SourceSha, identity.SourceIdentity);
    }

    /// <summary>
    /// Snapshot provenance is what stops "I refreshed from HOME, so this must be current": the operator can
    /// see where the data came from and how old it was.
    /// </summary>
    [Fact]
    public void Snapshot_provenance_is_reported_when_the_installation_was_refreshed_from_another()
    {
        var identity = RuntimeIdentityEndpoints.Resolve(Configuration(
            ("Instance:Label", "WORK-LAPTOP LOCAL"),
            ("Instance:Classification", "WorkLaptopLocal"),
            ("Instance:SnapshotSourceLabel", "HOME CANONICAL"),
            ("Instance:SnapshotSourceSha", "88497224ee90685bf28b330fc923dbdb218cb648"),
            ("Instance:SnapshotCreatedAtUtc", "2026-09-01T10:00:00.0000000Z")));

        Assert.Equal("WORK-LAPTOP LOCAL", identity.InstanceLabel);
        Assert.Equal("HOME CANONICAL", identity.SnapshotSourceLabel);
        Assert.Equal("2026-09-01T10:00:00.0000000Z", identity.SnapshotCreatedAtUtc);
    }

    /// <summary>Blank configuration is absent configuration, not a label made of spaces.</summary>
    [Fact]
    public void Whitespace_configuration_is_treated_as_undeclared()
    {
        var identity = RuntimeIdentityEndpoints.Resolve(Configuration(
            ("Runtime:Mode", "   "),
            ("Instance:Label", "")));

        Assert.Equal("UNKNOWN", identity.Mode);
        Assert.Equal("AEROLINK", identity.InstanceLabel);
    }
}
