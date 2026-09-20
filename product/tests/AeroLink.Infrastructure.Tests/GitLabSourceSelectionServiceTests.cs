using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class GitLabSourceSelectionServiceTests
{
    [Fact]
    public async Task Returning_to_a_commit_creates_a_new_event_and_preserves_all_prior_selections()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.SelectAsync('a', 0);
        var second = await fixture.SelectAsync('b', 1);
        var third = await fixture.SelectAsync('a', 2);
        Assert.Equal(3, await fixture.Db.GitLabSourceSelectionEvents.CountAsync());
        Assert.Equal(3, await fixture.Db.GitLabSourceSnapshots.CountAsync());
        var pointer = await fixture.Db.GitLabCurrentSourceSelections.AsNoTracking().SingleAsync();
        Assert.Equal(third.Id, pointer.SelectionEventId);
        Assert.Equal(3, pointer.Version);
        Assert.NotEqual(first.Id, third.Id);
        Assert.NotEqual(first.SourceSnapshotId, third.SourceSnapshotId);
        Assert.Equal(second.Id, (await fixture.Db.GitLabSourceSelectionEvents.AsNoTracking()
            .SingleAsync(x => x.ResultingVersion == 2)).Id);
    }

    [Fact]
    public async Task Stale_selection_or_moving_reference_cannot_replace_the_current_source()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.SelectAsync('a', 0);
        await Assert.ThrowsAsync<DomainException>(() => fixture.SelectAsync('b', 0));
        await Assert.ThrowsAsync<DomainException>(() => fixture.SelectAsync('b', 1, preview: new string('a', 40)));
        Assert.Equal(1, await fixture.Db.GitLabSourceSelectionEvents.CountAsync());
        Assert.Equal(first.Id, (await fixture.Db.GitLabCurrentSourceSelections.AsNoTracking().SingleAsync()).SelectionEventId);
    }

    [Fact]
    public async Task Configuration_changes_after_remote_observation_refuse_the_selection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var stale = await fixture.Db.ProjectRepositoryConfigurations.AsNoTracking().SingleAsync();
        var current = await fixture.Db.ProjectRepositoryConfigurations.SingleAsync();
        current.RecordVerification("tester", DateTimeOffset.UtcNow, 17, "group/project");
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainException>(() => fixture.SelectAsync('a', 0, configuration: stale));
        Assert.Empty(await fixture.Db.GitLabSourceSelectionEvents.ToListAsync());
    }

    [Fact]
    public async Task Released_build_source_cannot_be_initialized_through_the_normal_command()
    {
        await using var fixture = await Fixture.CreateAsync(released: true);
        await Assert.ThrowsAsync<DomainException>(() => fixture.SelectAsync('a', 0));
        Assert.Empty(await fixture.Db.GitLabSourceSnapshots.ToListAsync());
    }

    [Theory]
    [InlineData("https://different.example", "GitLab")]
    [InlineData("https://gitlab.example/wrong-prefix", "GitLab")]
    [InlineData("https://gitlab.example", "Other")]
    public async Task Observations_cannot_relabel_the_configured_repository_instance(string instance, string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider: provider);
        await Assert.ThrowsAsync<DomainException>(() => fixture.SelectAsync('a', 0, instance: instance));
        Assert.Empty(await fixture.Db.GitLabSourceSnapshots.ToListAsync());
    }

    [Fact]
    public async Task Review_freeze_refuses_source_changes_before_the_release_flag_is_set()
    {
        await using var fixture = await Fixture.CreateAsync(inReview: true);
        await Assert.ThrowsAsync<DomainException>(() => fixture.SelectAsync('a', 0));
        Assert.Empty(await fixture.Db.GitLabSourceSelectionEvents.ToListAsync());
    }

    private sealed class Fixture(SqliteConnection connection, AeroLinkDbContext db, Guid projectId, Guid releaseId) : IAsyncDisposable
    {
        public AeroLinkDbContext Db => db;
        public static async Task<Fixture> CreateAsync(bool released = false, string provider = "GitLab", bool inReview = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Source selection", "SRC");
            var project = new ProjectRecord(program.Id, "Source selection", "Source selection product");
            var release = new SoftwareRelease(project.Id, "1.0", released);
            var configuration = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
                provider, "https://gitlab.example/group/project", "tester", DateTimeOffset.UtcNow);
            configuration.RecordVerification("tester", DateTimeOffset.UtcNow, 17, "group/project");
            db.AddRange(program, project, release, configuration);
            if (inReview)
            {
                var now = DateTimeOffset.UtcNow;
                var baseline = new CandidateBaseline("BL-000001", 0, project.Id, release.Id, null, "Source review", "tester", now);
                var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Source review", "tester", now);
                campaign.StartVerification("tester", now);
                campaign.BeginReleaseReview("tester", [("approver", "Approver")], new string('a', 64), now);
                db.AddRange(baseline, campaign);
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new(connection, db, project.Id, release.Id);
        }

        public async Task<GitLabSourceSelectionEvent> SelectAsync(char sha, long version, string? preview = null,
            ProjectRepositoryConfiguration? configuration = null, string instance = "https://gitlab.example")
        {
            // Each call models a new request/context; retain no tracked pointer across requests.
            db.ChangeTracker.Clear();
            configuration ??= await db.ProjectRepositoryConfigurations.AsNoTracking().SingleAsync();
            await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, projectId);
            var result = await new GitLabSourceSelectionService(db).SelectAsync(scope, releaseId, configuration,
                instance, new(17, "main", GitLabReferenceKind.Branch, new string(sha, 40)),
                preview ?? new string(sha, 40), version, "tester", DateTimeOffset.UtcNow, default);
            await db.SaveChangesAsync();
            await scope.CommitAsync();
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
