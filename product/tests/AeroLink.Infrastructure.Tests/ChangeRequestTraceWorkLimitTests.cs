using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>Proof that bounded trace reads fail explicitly instead of silently doing unbounded work.</summary>
public sealed class ChangeRequestTraceWorkLimitTests
{
    [Fact]
    public async Task Rooted_trace_rejects_a_network_over_the_node_limit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.CreateChangeRequest("SRCR-10000");
        var children = Enumerable.Range(1, 1000)
            .Select(i => fixture.CreateChangeRequest($"SRCR-{10000 + i:00000}")).ToList();
        foreach (var child in children)
            child.AddUpstreamLink("author", root.Id, root.DisplayNumber, fixture.Release.Id,
                fixture.Release.Version, "bounded trace test", fixture.Now);
        fixture.Db.Add(root);
        fixture.Db.AddRange(children);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<TraceWorkLimitException>(() => ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task Rooted_trace_rejects_more_than_twenty_thousand_relation_rows_before_materializing_the_network()
    {
        await using var fixture = await Fixture.CreateAsync();
        var changes = Enumerable.Range(0, 202)
            .Select(i => fixture.CreateChangeRequest($"SRCR-{10100 + i:00000}")).ToList();
        var root = changes[0];

        // 201 children each name 100 distinct parents. The graph stays well under the 1,000-node ceiling,
        // while the set-based relation read exceeds the independent 20,000-row budget.
        for (var childIndex = 1; childIndex < changes.Count; childIndex++)
        {
            var parents = changes.Where((_, index) => index != childIndex).Take(100);
            foreach (var parent in parents)
                changes[childIndex].AddUpstreamLink("author", parent.Id, parent.DisplayNumber,
                    fixture.Release.Id, fixture.Release.Version, "row budget test", fixture.Now);
        }
        fixture.Db.AddRange(changes);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<TraceWorkLimitException>(() => ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task Rooted_trace_rejects_a_chain_deeper_than_sixty_four_waves()
    {
        await using var fixture = await Fixture.CreateAsync();
        var changes = Enumerable.Range(0, 66)
            .Select(i => fixture.CreateChangeRequest($"SRCR-{10200 + i:00000}")).ToList();
        for (var i = 1; i < changes.Count; i++)
            changes[i].AddUpstreamLink("author", changes[i - 1].Id, changes[i - 1].DisplayNumber,
                fixture.Release.Id, fixture.Release.Version, "depth budget test", fixture.Now);
        fixture.Db.AddRange(changes);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<TraceWorkLimitException>(() => ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, changes[0].Id, LegacyLadderPolicy.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task Build_network_cap_is_deterministic_and_declares_truncation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var changes = Enumerable.Range(0, 5)
            .Select(i => fixture.CreateChangeRequest($"SRCR-{10300 + i:00000}")).ToList();
        fixture.Db.AddRange(changes);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var first = await ChangeRequestTraceProjection.ForBuildAsync(
            fixture.Db, fixture.Project.Id, fixture.Release.Id, LegacyLadderPolicy.Instance, 2, CancellationToken.None);
        var second = await ChangeRequestTraceProjection.ForBuildAsync(
            fixture.Db, fixture.Project.Id, fixture.Release.Id, LegacyLadderPolicy.Instance, 2, CancellationToken.None);

        Assert.True(first.Truncated);
        Assert.Equal(2, first.Nodes.Count);
        Assert.Empty(first.Edges);
        Assert.Equal(first.Nodes.Select(x => x.Id), second.Nodes.Select(x => x.Id));
        Assert.True(first.Nodes.All(x => x.ProjectId == fixture.Project.Id));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, AeroLinkDbContext db, ProjectRecord project,
            SoftwareRelease release, DateTimeOffset now)
        { _connection = connection; Db = db; Project = project; Release = release; Now = now; }
        public AeroLinkDbContext Db { get; }
        public ProjectRecord Project { get; }
        public SoftwareRelease Release { get; }
        public DateTimeOffset Now { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
            var program = new ProgramRecord("Trace limits", "TLM");
            var project = new ProjectRecord(program.Id, "Trace limits", "Trace limits product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            db.AddRange(program, project, release);
            await db.SaveChangesAsync();
            return new(connection, db, project, release, now);
        }

        public SystemChangeRequest CreateChangeRequest(string number) =>
            new(number, 0, Project.Id, Release.Id, number, "Problem", "Analysis", "Solution", "author", Now);

        public async ValueTask DisposeAsync()
        { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
