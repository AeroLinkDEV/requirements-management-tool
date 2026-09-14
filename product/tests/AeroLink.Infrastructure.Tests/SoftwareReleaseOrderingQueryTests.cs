using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class SoftwareReleaseOrderingQueryTests
{
    [Fact]
    public async Task Canonical_keyset_pages_preserve_historical_versions_on_sqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await AssertCanonicalPagesAsync(db);
    }

    internal static async Task AssertCanonicalPagesAsync(AeroLinkDbContext db)
    {
        var program = new ProgramRecord("Ordering fixture", "ORDERING");
        var project = new ProjectRecord(program.Id, "Ordering fixture", "Software");
        var releases = new[] { "10.5", "9.0", "1.3", "2.0", "0.01", "99.99", "20.1", "21.0", "22.0" }
            .Select(version => new SoftwareRelease(project.Id, version, false)).ToArray();
        // Historical rows may have no persisted canonical key, including equivalent raw version forms.
        foreach (var release in releases.Where(x => x.Version is "2.0" or "9.0"))
            typeof(SoftwareRelease).GetProperty(nameof(SoftwareRelease.CanonicalIdentity))!
                .SetValue(release, null);
        db.AddRange(program, project);
        db.AddRange(releases);
        await db.SaveChangesAsync();
        // Reproduce a pre-hardening row in this owned disposable fixture. Normal new writes correctly reject
        // this equivalent identity, so the fixture alone bypasses that new-write authority.
        var historical = releases.Single(x => x.Version == "2.0");
        await db.Releases.Where(x => x.Id == historical.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Version, "1.30").SetProperty(x => x.CanonicalIdentity, (string?)null));
        typeof(SoftwareRelease).GetProperty(nameof(SoftwareRelease.Version))!.SetValue(historical, "1.30");
        foreach (var (version, raw, stored) in new (string, string, string?)[]
        {
            ("20.1", " prior label ", " sw-20.10 "),
            ("21.0", " invalid label ", null),
            ("22.0", " Different label ", " Legacy-name "),
        })
        {
            var row = releases.Single(x => x.Version == version);
            await db.Releases.Where(x => x.Id == row.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Version, raw).SetProperty(x => x.CanonicalIdentity, stored));
            typeof(SoftwareRelease).GetProperty(nameof(SoftwareRelease.Version))!.SetValue(row, raw);
            typeof(SoftwareRelease).GetProperty(nameof(SoftwareRelease.CanonicalIdentity))!.SetValue(row, stored);
        }
        db.ChangeTracker.Clear();

        var result = new List<SoftwareRelease>();
        string? cursor = null;
        for (var page = 0; page <= releases.Length; page++)
        {
            var query = SoftwareReleaseOrderingQuery.WithSortKey(db.Releases.AsNoTracking().Where(x => x.ProjectId == project.Id),
                db.Database.IsNpgsql() ? "C" : "BINARY");
            if (cursor is not null) query = query.Where(x => string.Compare(x.SortKey, cursor) > 0);
            var rows = await query.OrderBy(x => x.SortKey).Take(1).ToListAsync();
            if (rows.Count == 0) break;
            result.Add(rows[0].Release);
            Assert.Equal(SoftwareReleaseOrdering.Key(rows[0].Release), rows[0].SortKey);
            cursor = rows[0].SortKey;
        }
        Assert.Equal(SoftwareReleaseOrdering.Ascending(releases).Select(x => x.Id), result.Select(x => x.Id));
        Assert.Equal(["0.01", "1.3", "1.30", "9.0", "10.5", " prior label ", "99.99", " Different label ", " invalid label "], result.Select(x => x.Version));
        Assert.Equal(releases.Length, result.Select(x => x.Id).Distinct().Count());
        Assert.Null(result.Single(x => x.Version == "1.30").CanonicalIdentity);
    }
}
