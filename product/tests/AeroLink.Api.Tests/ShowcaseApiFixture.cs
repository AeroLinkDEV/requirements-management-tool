using System.Net;
using System.Net.Http.Json;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api.Tests;

/// <summary>
/// The FMS showcase seeded once for this assembly, so tests that need it start from a copy.
///
/// Three tests here — one configuration publication and two draft-document journeys — each called
/// `EnsureSeededAsync` inside their own `AeroLinkApiFactory`. That is 85, 64 and 27 seconds against a median
/// test of 3.9 seconds, and all three built the same 1,250-requirement dataset. The infrastructure suite
/// already solved this by seeding a template and copying the file per test; this is the same fixture on the
/// other side of the API boundary.
///
/// The template is seeded through a bare context rather than through the API, because the showcase seeder is
/// the thing being reused, not the thing being tested. What is being tested still runs over HTTP against a
/// private database.
/// </summary>
public sealed class ShowcaseApiFixture : IAsyncLifetime
{
    private string _templatePath = string.Empty;

    /// <summary>Identifiers from the one seed, stable across every copy taken from it.</summary>
    public FmsShowcaseSummary Summary { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _templatePath = Path.Combine(Path.GetTempPath(), $"aerolink-api-showcase-template-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={_templatePath};Pooling=False").Options;
        await using (var db = new AeroLinkDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            // Controlled FMS closure evidence must be attributable to the seeded SQA account.
            await new IdentitySeeder(db).EnsureSeededAsync();
            Summary = await new FmsShowcaseSeeder(db).EnsureSeededAsync();
            // Production performs a second identity pass after all showcase Programs exist. Keep this template
            // in the same state as a real demo startup so HTTP tests exercise the post-Program authority graph.
            await new IdentitySeeder(db).EnsureSeededAsync();
        }
        // Clearing pools is what guarantees the handle is released before anything copies the file. A copy taken
        // mid-write fails in whichever test happened to take it, which is the hardest kind of failure to read.
        SqliteConnection.ClearAllPools();
    }

    public Task DisposeAsync()
    {
        try { if (File.Exists(_templatePath)) File.Delete(_templatePath); } catch (IOException) { }
        return Task.CompletedTask;
    }

    /// <summary>A factory whose database begins as a copy of the seeded showcase.</summary>
    internal AeroLinkApiFactory CreateFactory(bool enableEnterpriseJobWorker = false) =>
        new(showcaseTemplate: _templatePath, enableEnterpriseJobWorker: enableEnterpriseJobWorker);

    /// <summary>Signs in through the identity that already belongs to the copied showcase.</summary>
    internal static async Task LoginAdministratorAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName = IdentityService.SystemAdministratorUserName,
            password = IdentitySeeder.DemoPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}

/// <summary>
/// Classes here run sequentially against one shared template. That is the trade: the three showcase tests queue
/// behind each other instead of running in parallel, and each is seconds rather than a minute.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ShowcaseApiCollection : ICollectionFixture<ShowcaseApiFixture>
{
    public const string Name = "FMS showcase over HTTP";
}
