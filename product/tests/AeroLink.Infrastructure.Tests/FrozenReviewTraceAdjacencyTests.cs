using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Semantic coverage for the disposable frozen-review adjacency projection.
///
/// These tests deliberately keep the review snapshot as the evidence authority. The adjacency table is
/// exercised as a rebuildable lookup only: deleting it must not change signed snapshot bytes, and malformed
/// snapshots must not turn a migration into an unbounded or partially completed operation.
/// </summary>
public sealed class FrozenReviewTraceAdjacencyTests
{
    [Fact]
    public async Task Frozen_review_trace_walks_parent_to_child_without_a_live_link_and_keeps_exact_provenance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var earlier = new SoftwareRelease(fixture.Project.Id, "0.9", false);
        var parent = fixture.CreateChangeRequest("SRCR-09000", earlier.Id);
        var child = fixture.CreateChangeRequest("SRCR-09001", fixture.Release.Id);
        var assessmentId = Guid.NewGuid();
        var assessmentLinkId = Guid.NewGuid();
        var snapshot = Snapshot(parent.Id, assessmentId, assessmentLinkId, earlier.Id);
        var cycle = fixture.CreateCycle(child, snapshot);
        fixture.Db.AddRange(earlier, parent, child, cycle);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        Assert.Empty(await fixture.Db.ChangeRequestUpstreamLinks.AsNoTracking().ToListAsync());
        Assert.Contains(await fixture.Db.Set<FrozenReviewTraceLink>().AsNoTracking().ToListAsync(),
            x => x.CycleId == cycle.Id && x.OwnerId == child.Id && x.UpstreamId == parent.Id);

        var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, child.Id, LegacyLadderPolicy.Instance, CancellationToken.None);

        var edge = Assert.Single(result!.Edges, x => x.FromId == parent.Id && x.ToId == child.Id);
        var provenance = Assert.Single(edge.Provenance, x => x.Kind == "FrozenReviewEvidence");
        Assert.False(provenance.IsLive);
        Assert.Equal(assessmentId, provenance.AssessmentId);
        Assert.Equal(assessmentLinkId, provenance.AssessmentLinkId);
        Assert.Equal(earlier.Id, provenance.BuildId);
        Assert.Equal("0.9", provenance.BuildVersion);
        Assert.Contains(result.Nodes, x => x.Id == parent.Id && x.BuildVersion == "0.9");
        Assert.Contains(result.Nodes, x => x.Id == child.Id && x.BuildVersion == "1.0");
    }

    [Fact]
    public async Task Backfill_rebuilds_deleted_adjacency_without_mutating_snapshots_and_is_marker_idempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var parent = fixture.CreateChangeRequest("SRCR-09100", fixture.Release.Id);
        var cycles = new List<ReviewCycle>();
        for (var i = 0; i < 101; i++)
        {
            var child = fixture.CreateChangeRequest($"SRCR-{9101 + i:00000}", fixture.Release.Id);
            var snapshot = Snapshot(parent.Id, Guid.NewGuid(), Guid.NewGuid(), fixture.Release.Id);
            var cycle = fixture.CreateCycle(child, snapshot);
            fixture.Db.AddRange(child, cycle);
            cycles.Add(cycle);
        }
        fixture.Db.Add(parent);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var originals = await fixture.Db.ReviewCycles.AsNoTracking()
            .Where(x => cycles.Select(y => y.Id).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => (x.SnapshotJson, x.SnapshotHash));
        await fixture.Db.Database.ExecuteSqlRawAsync("DELETE FROM frozen_review_trace_links");

        var authority = new FrozenReviewTraceAdjacencyMigrationAuthority(fixture.Db);
        await authority.EnsureCompletedAsync();

        Assert.Equal(101, await fixture.Db.Set<FrozenReviewTraceLink>().CountAsync());
        Assert.Equal(1, await fixture.Db.GovernedMigrationCompletions.CountAsync(x =>
            x.Marker == FrozenReviewTraceAdjacencyMigrationAuthority.Marker));
        foreach (var row in await fixture.Db.ReviewCycles.AsNoTracking()
                     .Where(x => cycles.Select(y => y.Id).Contains(x.Id)).ToListAsync())
        {
            Assert.Equal(originals[row.Id], (row.SnapshotJson, row.SnapshotHash));
        }

        await authority.EnsureCompletedAsync();
        Assert.Equal(101, await fixture.Db.Set<FrozenReviewTraceLink>().CountAsync());
        Assert.Equal(1, await fixture.Db.GovernedMigrationCompletions.CountAsync(x =>
            x.Marker == FrozenReviewTraceAdjacencyMigrationAuthority.Marker));
    }

    [Fact]
    public async Task Malformed_or_non_object_snapshots_are_tolerated_and_do_not_create_false_adjacency()
    {
        await using var fixture = await Fixture.CreateAsync();
        var parent = fixture.CreateChangeRequest("SRCR-09200", fixture.Release.Id);
        var malformedChild = fixture.CreateChangeRequest("SRCR-09201", fixture.Release.Id);
        var arrayChild = fixture.CreateChangeRequest("SRCR-09202", fixture.Release.Id);
        var scalarChild = fixture.CreateChangeRequest("SRCR-09203", fixture.Release.Id);
        var nullChild = fixture.CreateChangeRequest("SRCR-09204", fixture.Release.Id);
        var malformed = fixture.CreateCycle(malformedChild, "{\"derivedLinks\":[");
        var array = fixture.CreateCycle(arrayChild, "[\"not-a-snapshot\"]");
        var scalar = fixture.CreateCycle(scalarChild, "42");
        var nullSnapshot = fixture.CreateCycle(nullChild, "null");
        fixture.Db.AddRange(parent, malformedChild, arrayChild, scalarChild, nullChild,
            malformed, array, scalar, nullSnapshot);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await new FrozenReviewTraceAdjacencyMigrationAuthority(fixture.Db).EnsureCompletedAsync();

        Assert.Empty(await fixture.Db.Set<FrozenReviewTraceLink>().AsNoTracking()
            .Where(x => x.OwnerId == malformedChild.Id || x.OwnerId == arrayChild.Id
                || x.OwnerId == scalarChild.Id || x.OwnerId == nullChild.Id).ToListAsync());
        Assert.Equal(1, await fixture.Db.GovernedMigrationCompletions.CountAsync(x =>
            x.Marker == FrozenReviewTraceAdjacencyMigrationAuthority.Marker));
        var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, malformedChild.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Nodes, x => x.Id == parent.Id);
    }

    [Fact]
    public async Task Frozen_parent_in_another_project_is_not_traversed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var otherProgram = new ProgramRecord("Other", "OTH");
        var otherProject = new ProjectRecord(otherProgram.Id, "Other project", "Other product");
        var otherRelease = new SoftwareRelease(otherProject.Id, "1.0", false);
        var foreignParent = fixture.CreateChangeRequest("SRCR-09300", otherRelease.Id, otherProject.Id);
        var child = fixture.CreateChangeRequest("SRCR-09301", fixture.Release.Id);
        var cycle = fixture.CreateCycle(child, Snapshot(foreignParent.Id, Guid.NewGuid(), Guid.NewGuid(), otherRelease.Id));
        fixture.Db.AddRange(otherProgram, otherProject, otherRelease, foreignParent, child, cycle);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await new FrozenReviewTraceAdjacencyMigrationAuthority(fixture.Db).EnsureCompletedAsync();
        Assert.Contains(await fixture.Db.Set<FrozenReviewTraceLink>().AsNoTracking().ToListAsync(),
            x => x.ProjectId == fixture.Project.Id && x.OwnerId == child.Id && x.UpstreamId == foreignParent.Id);

        var result = await ChangeRequestTraceProjection.ForChangeRequestAsync(
            fixture.Db, fixture.Project.Id, child.Id, LegacyLadderPolicy.Instance, CancellationToken.None);
        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Nodes, x => x.Id == foreignParent.Id);
        Assert.DoesNotContain(result.Edges, x => x.FromId == foreignParent.Id || x.ToId == foreignParent.Id);
    }

    [Fact]
    public async Task Cancellation_does_not_leave_a_false_completion_and_the_connection_remains_usable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var parent = fixture.CreateChangeRequest("SRCR-09400", fixture.Release.Id);
        var child = fixture.CreateChangeRequest("SRCR-09401", fixture.Release.Id);
        var cycle = fixture.CreateCycle(child, Snapshot(parent.Id, Guid.NewGuid(), Guid.NewGuid(), fixture.Release.Id));
        fixture.Db.AddRange(parent, child, cycle);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        using var cancellation = new CancellationTokenSource();
        // Model legacy data: the durable review snapshot remains, while only the disposable derived rows are
        // absent. Cancellation must not turn this interrupted run into a false migration completion.
        await fixture.Db.Database.ExecuteSqlRawAsync("DELETE FROM frozen_review_trace_links");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FrozenReviewTraceAdjacencyMigrationAuthority(fixture.Db).EnsureCompletedAsync(cancellation.Token));

        Assert.Equal(0, await fixture.Db.GovernedMigrationCompletions.CountAsync(x =>
            x.Marker == FrozenReviewTraceAdjacencyMigrationAuthority.Marker));
        Assert.Equal(0, await fixture.Db.Set<FrozenReviewTraceLink>().CountAsync());
        Assert.Equal(2, await fixture.Db.SystemChangeRequests.CountAsync());
        await new FrozenReviewTraceAdjacencyMigrationAuthority(fixture.Db).EnsureCompletedAsync();
        Assert.Equal(1, await fixture.Db.GovernedMigrationCompletions.CountAsync(x =>
            x.Marker == FrozenReviewTraceAdjacencyMigrationAuthority.Marker));
    }

    private static string Snapshot(Guid parentId, Guid assessmentId, Guid assessmentLinkId, Guid buildId) =>
        JsonSerializer.Serialize(new
        {
            contractVersion = 3,
            authoredLinks = Array.Empty<object>(),
            derivedLinks = new[] { new
            {
                upstreamChangeRequestId = parentId,
                assessmentId,
                assessmentLinkId,
                buildId
            }
        }});

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, AeroLinkDbContext db, ProgramRecord program,
            ProjectRecord project, SoftwareRelease release, DateTimeOffset now)
        { _connection = connection; Db = db; Program = program; Project = project; Release = release; Now = now; }
        public AeroLinkDbContext Db { get; }
        public ProgramRecord Program { get; }
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
            var program = new ProgramRecord("Frozen trace", "FTR");
            var project = new ProjectRecord(program.Id, "Frozen project", "Frozen product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            db.AddRange(program, project, release);
            await db.SaveChangesAsync();
            return new(connection, db, program, project, release, now);
        }

        public SystemChangeRequest CreateChangeRequest(string number, Guid releaseId, Guid? projectId = null) =>
            new(number, 0, projectId ?? Project.Id, releaseId, number, "Problem", "Analysis", "Solution", "author", Now);

        public ReviewCycle CreateCycle(SystemChangeRequest child, string snapshot)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
            return new ReviewCycle(child.Id, 1, hash,
                [new ApproverSelection("reviewer", "Reviewer")], Now,
                snapshotContractVersion: 3, snapshotJson: snapshot);
        }

        public async ValueTask DisposeAsync()
        { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
