using System.Diagnostics;
using System.Text.Json;

namespace AeroLink.Api.Tests;

/// <summary>
/// #1614 1.6: /health/identity reports the product version, and reports it to a non-loopback caller, because the
/// release updater confirms a promoted version at the public HTTPS name. A build that was not given a release
/// version (every build except Build-AeroLinkRelease.ps1, including this test host) must say so rather than
/// claim the SDK's default 1.0.0, which would read exactly like the first real release.
/// </summary>
public sealed class RuntimeIdentityVersionTests
{
    [Fact]
    public async Task Identity_reports_the_unreleased_development_version_to_a_non_loopback_caller()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/identity");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The test server has no remote address, so this is the LAN-browser answer: the revision is withheld.
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sourceSha").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sourceIdentity").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sourceShortSha").ValueKind);
        Assert.Equal("0.0.0-dev", json.RootElement.GetProperty("version").GetString());

        // This expected value is read independently from the operating system in the test process. It is the
        // same process that hosts the TestServer, so the assertion proves the public identity reports the OS
        // process creation instant needed for an updater to join the redacted response to its owned service.
        using var process = Process.GetCurrentProcess();
        var expectedProcessStartedAtUtc = process.StartTime.ToUniversalTime();
        Assert.Equal(expectedProcessStartedAtUtc, json.RootElement.GetProperty("startedAtUtc").GetDateTimeOffset());
    }
}
