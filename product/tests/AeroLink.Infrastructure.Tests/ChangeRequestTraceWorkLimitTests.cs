using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace AeroLink.Infrastructure.Tests;

/// <summary>Proof that bounded trace reads fail explicitly instead of silently doing unbounded work.</summary>
public sealed class ChangeRequestTraceWorkLimitTests
{
    [Fact]
    public async Task Direct_trace_does_not_expand_a_parents_large_connected_component()
    {
        await using var fixture = await Fixture.CreateAsync();
        var parent = fixture.CreateChangeRequest("SRCR-11000");
        var children = Enumerable.Range(1, 1000)
            .Select(i => fixture.CreateChangeRequest($"SRCR-{11000 + i:00000}")).ToList();
        foreach (var child in children)
            child.AddUpstreamLink("author", parent.Id, parent.DisplayNumber, fixture.Release.Id,
                fixture.Release.Version, "Exact parent", fixture.Now);
        fixture.Db.Add(parent);
        fixture.Db.AddRange(children);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var root = children[0];

        await Assert.ThrowsAsync<TraceWorkLimitException>(() => ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None));
        var direct = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, root.Id, LegacyLadderPolicy.Instance, CancellationToken.None, directOnly: true);
        Assert.NotNull(direct);
        Assert.True(direct.DirectOnly);
        Assert.Equal(2, direct.Nodes.Count);
        Assert.Contains(direct.Nodes, node => node.Id == parent.Id);
        Assert.Contains(direct.Nodes, node => node.Id == root.Id);
        var edge = Assert.Single(direct.Edges);
        Assert.Equal(parent.Id, edge.FromId);
        Assert.Equal(root.Id, edge.ToId);
        // An over-large immediate neighbourhood still refuses; this is not a budget bypass.
        await Assert.ThrowsAsync<TraceWorkLimitException>(() => ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, parent.Id, LegacyLadderPolicy.Instance, CancellationToken.None, directOnly: true));
    }

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
    public async Task Rooted_trace_cancellation_mid_frontier_read_leaves_the_context_usable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.CreateChangeRequest("SRCR-10280");
        var child = fixture.CreateChangeRequest("SRCR-10281");
        child.AddUpstreamLink("author", root.Id, root.DisplayNumber, fixture.Release.Id,
            fixture.Release.Version, "mid-frontier cancellation test", fixture.Now);
        fixture.Db.AddRange(root, child);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        using var cancellation = new CancellationTokenSource();
        fixture.Interceptor.Arm(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, child.Id, LegacyLadderPolicy.Instance, cancellation.Token));
        Assert.Equal(3, fixture.Interceptor.CancellationReaderNumber);

        fixture.Interceptor.Disarm();
        Assert.Equal(2, await fixture.Db.SystemChangeRequests.CountAsync());
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
            SoftwareRelease release, DateTimeOffset now, MidFrontierCancellationInterceptor interceptor)
        { _connection = connection; Db = db; Project = project; Release = release; Now = now; Interceptor = interceptor; }
        public AeroLinkDbContext Db { get; }
        public ProjectRecord Project { get; }
        public SoftwareRelease Release { get; }
        public DateTimeOffset Now { get; }
        public MidFrontierCancellationInterceptor Interceptor { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var interceptor = new MidFrontierCancellationInterceptor();
            var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>()
                .UseSqlite(connection).AddInterceptors(interceptor).Options);
            await db.Database.EnsureCreatedAsync();
            var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
            var program = new ProgramRecord("Trace limits", "TLM");
            var project = new ProjectRecord(program.Id, "Trace limits", "Trace limits product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            db.AddRange(program, project, release);
            await db.SaveChangesAsync();
            return new(connection, db, project, release, now, interceptor);
        }

        public SystemChangeRequest CreateChangeRequest(string number) =>
            new(number, 0, Project.Id, Release.Id, number, "Problem", "Analysis", "Solution", "author", Now);

        public async ValueTask DisposeAsync()
        { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class MidFrontierCancellationInterceptor : DbCommandInterceptor
    {
        private CancellationTokenSource? _source;
        private int _readerCount;
        private int _cancellationReaderNumber;
        public int CancellationReaderNumber => Volatile.Read(ref _cancellationReaderNumber);

        public void Arm(CancellationTokenSource source)
        {
            _source = source;
            Interlocked.Exchange(ref _readerCount, 0);
            Interlocked.Exchange(ref _cancellationReaderNumber, 0);
        }

        public void Disarm()
        {
            _source = null;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var source = _source;
            if (source is not null)
            {
                var readerNumber = Interlocked.Increment(ref _readerCount);
                if (readerNumber == 3)
                {
                    Interlocked.Exchange(ref _cancellationReaderNumber, readerNumber);
                    source.Cancel();
                }
            }
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }
}
