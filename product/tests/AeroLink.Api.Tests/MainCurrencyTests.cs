using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;

namespace AeroLink.Api.Tests;

public sealed class MainCurrencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aerolink-main-currency-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    private const string Running = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Newer = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public MainCurrencyTests() => Directory.CreateDirectory(root);

    private IConfiguration Configuration(string mode = "HOME-PRODUCTION", string classification = "HomeCanonical") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Runtime:SourceSha"] = Running, ["Runtime:SourceIdentity"] = Running,
            ["Runtime:SourceRoot"] = root, ["Runtime:MainCurrencyPath"] = Path.Combine(root, "currency.json"),
            ["Runtime:Mode"] = mode, ["Instance:Classification"] = classification,
        }).Build();

    private void Write(string? sourceSha = Running, string? remoteSha = Running, bool verified = true,
        int ageMinutes = 1, string? sourceRoot = null) =>
        File.WriteAllText(Path.Combine(root, "currency.json"), JsonSerializer.Serialize(new
        {
            sourceRoot = sourceRoot ?? root, sourceSha, remoteSha, verified,
            checkedAtUtc = now.AddMinutes(-ageMinutes),
        }));

    [Theory]
    [InlineData(Running, Running, true, 1, "Current")]
    [InlineData(Running, Newer, true, 1, "UpdateAvailable")]
    [InlineData(Running, Running, false, 1, "Unverified")]
    [InlineData(Running, Newer, false, 1, "Unverified")]
    [InlineData(Running, Running, true, 31, "Unverified")]
    [InlineData(Running, Running, true, -1, "Unverified")]
    [InlineData(Newer, Newer, true, 1, "Unverified")]
    [InlineData(Running, "unknown", true, 1, "Unverified")]
    public void Currency_requires_fresh_remote_proof_bound_to_the_actual_running_source(
        string sourceSha, string remoteSha, bool verified, int ageMinutes, string expected)
    {
        Write(sourceSha, remoteSha, verified, ageMinutes);
        var config = Configuration();
        Assert.Equal(expected, MainCurrency.Read(config, RuntimeIdentityEndpoints.Resolve(config), now)!.State);
    }

    [Fact]
    public void Missing_malformed_or_foreign_observation_never_becomes_current()
    {
        var config = Configuration();
        MainCurrencyStatus Read() => MainCurrency.Read(config, RuntimeIdentityEndpoints.Resolve(config), now)!;
        Assert.Equal("Unverified", Read().State);
        File.WriteAllText(Path.Combine(root, "currency.json"), "{partial");
        Assert.Equal("Unverified", Read().State);
        Write(sourceRoot: Path.Combine(root, "another-source"));
        Assert.Equal("Unverified", Read().State);
        Assert.Null(Read().CheckedAtUtc);
        Write();
        var path = Path.Combine(root, "currency.json");
        File.WriteAllText(path, File.ReadAllText(path), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal("Current", Read().State); // Windows PowerShell Set-Content -Encoding UTF8 emits a BOM.
    }

    [Fact]
    public void Passive_reads_observe_controller_replacement_without_writing_anything()
    {
        var config = Configuration();
        var runtime = RuntimeIdentityEndpoints.Resolve(config);
        Write();
        var path = Path.Combine(root, "currency.json");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("Current", MainCurrency.Read(config, runtime, now)!.State);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Write(remoteSha: Newer);
        Assert.Equal("UpdateAvailable", MainCurrency.Read(config, runtime, now)!.State);
        Write(verified: false);
        Assert.Equal("Unverified", MainCurrency.Read(config, runtime, now)!.State);
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("DEVELOPMENT", "HomeCanonical")]
    [InlineData("HOME-PRODUCTION", "WorkLaptopLocal")]
    public void Other_modes_and_installations_have_no_HOME_currency(string mode, string classification)
    {
        Write();
        var config = Configuration(mode, classification);
        Assert.Null(MainCurrency.Read(config, RuntimeIdentityEndpoints.Resolve(config), now));
    }

    [Fact]
    public async Task Anonymous_identity_exposes_only_currency_summary_and_disables_caching()
    {
        Write(remoteSha: Newer);
        using var factory = new AeroLinkApiFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddConfiguration(Configuration())));
        using var client = configured.CreateClient();
        using var response = await client.GetAsync("/health/identity");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var currency = json.RootElement.GetProperty("mainCurrency");
        Assert.Equal("UpdateAvailable", currency.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, currency.GetProperty("remoteSha").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sourceSha").ValueKind);
        Assert.DoesNotContain(root, json.RootElement.GetRawText());
        Assert.Single(Directory.GetFiles(root));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
