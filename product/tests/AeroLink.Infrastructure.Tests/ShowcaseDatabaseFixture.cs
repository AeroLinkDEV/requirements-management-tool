using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The FMSLIVE showcase seeded once per test run and copied per test, rather than rebuilt per test.
///
/// Seeding builds 150 system requirements, 400 high-level, 700 low-level, 105 change requests, their reviews,
/// baselines, documents and verification evidence. It took between 36 and 69 seconds, and eight tests each did
/// it from scratch: 427 seconds of the infrastructure suite's 189-second wall clock, or about 94% of it once
/// parallelism is accounted for. Nothing else in the suite came close — the slowest test that does not seed the
/// showcase runs in 931 ms.
///
/// SQLite keeps a database in one file, so the fix is a file copy. The showcase is seeded once into a template,
/// and each test gets its own copy to mutate. Isolation is unchanged: every test still owns a private database
/// and can approve, freeze and release inside it without any other test seeing it. What is no longer repeated is
/// the building of a dataset that was identical all eight times.
///
/// `FmsShowcaseSeederTests` deliberately does not use this. That test exists to prove the seeder produces the
/// dataset and is idempotent when run twice, which cannot be demonstrated against a copy of its own output.
/// </summary>
public sealed class ShowcaseDatabaseFixture : IAsyncLifetime
{
    private string _templatePath = string.Empty;

    /// <summary>
    /// The identifiers the seeder produced. Stable across every copy, because there was only ever one seed.
    /// </summary>
    public FmsShowcaseSummary Summary { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _templatePath = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-template-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={_templatePath};Pooling=False").Options;
        await using (var db = new AeroLinkDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            // The FMS seeder freezes controlled closure evidence. Seed the directory first so the
            // package records the real quality.analyst account rather than a synthetic empty identity.
            await new IdentitySeeder(db).EnsureSeededAsync();
            Summary = await new FmsShowcaseSeeder(db).EnsureSeededAsync();
        }
        // Pooling is off on the connection string, but clearing pools is what guarantees the file handle is
        // released before anything copies it. A copy taken while a connection is still open is a copy of a
        // database mid-write, and the failure lands in whichever test happened to take it.
        SqliteConnection.ClearAllPools();
    }

    public Task DisposeAsync()
    {
        TryDelete(_templatePath);
        return Task.CompletedTask;
    }

    /// <summary>A private, writable copy of the seeded showcase. Dispose it to remove the file.</summary>
    public ShowcaseDatabase Create()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-{Guid.NewGuid():N}.db");
        File.Copy(_templatePath, path);
        return new ShowcaseDatabase(path);
    }

    internal static void TryDelete(string path)
    {
        // A test that leaves a connection open would otherwise fail in teardown rather than on its assertion,
        // which hides the real failure behind a file lock.
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}

public sealed class ShowcaseDatabase(string path) : IDisposable
{
    public string Path { get; } = path;

    public DbContextOptions<AeroLinkDbContext> Options { get; } =
        new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;

    public AeroLinkDbContext Context() => new(Options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        ShowcaseDatabaseFixture.TryDelete(Path);
    }
}

/// <summary>
/// Classes in one collection run sequentially, which is the trade this makes: three classes that used to run in
/// parallel now queue behind each other, and each is seconds rather than a minute.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ShowcaseCollection : ICollectionFixture<ShowcaseDatabaseFixture>
{
    public const string Name = "FMSLIVE showcase";
}
