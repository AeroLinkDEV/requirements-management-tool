using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ChangeRequestRepositoryLoadTests
{
    [Fact]
    public async Task Detail_loads_complete_response_history_without_loading_private_comments()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var measurement = new QueryReadMeasurement { Enabled = true };
        var options = fixture.Options(measurement);

        await using var db = new AeroLinkDbContext(options);
        var request = await new ChangeRequestRepository(db).GetAsync(fixture.RequestId,
            ChangeRequestLoadShape.Detail, CancellationToken.None);

        Assert.NotNull(request);
        Assert.NotEmpty(request!.RequirementChanges);
        Assert.NotEmpty(request.ReviewCycles);
        Assert.NotEmpty(request.ReviewCycles.Single().Steps);
        Assert.NotEmpty(request.AuditEvents);
        Assert.NotEmpty(request.UpstreamLinks);
        Assert.NotEmpty(request.UpstreamHistory);
        Assert.Empty(request.ReviewCycles.Single().Comments);
        Assert.True(measurement.Statements.Count >= 6);
        Assert.True(measurement.Rows >= 6);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Review_comment_shape_loads_discussion_without_purchasing_unrelated_history()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var measurement = new QueryReadMeasurement { Enabled = true };
        await using var db = new AeroLinkDbContext(fixture.Options(measurement));

        var request = await new ChangeRequestRepository(db).GetAsync(fixture.RequestId,
            ChangeRequestLoadShape.ReviewDiscussion, CancellationToken.None);

        Assert.NotNull(request);
        Assert.NotEmpty(request!.ReviewCycles);
        Assert.NotEmpty(request.ReviewCycles.Single().Steps);
        Assert.NotEmpty(request.ReviewCycles.Single().Comments);
        Assert.NotEmpty(request.RequirementChanges);
        Assert.Empty(request.AuditEvents);
        Assert.Empty(request.UpstreamLinks);
        Assert.Empty(request.UpstreamHistory);
        Assert.False(db.Entry(request).Collection(x => x.AuditEvents).IsLoaded);
        Assert.False(db.Entry(request).Collection(x => x.UpstreamLinks).IsLoaded);
        Assert.False(db.Entry(request).Collection(x => x.UpstreamHistory).IsLoaded);
        Assert.True(measurement.Statements.Count >= 4);
        Assert.True(measurement.Rows >= 4);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Scalar_shape_uses_one_query_and_does_not_materialize_child_collections()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var measurement = new QueryReadMeasurement { Enabled = true };
        await using var db = new AeroLinkDbContext(fixture.Options(measurement));

        var request = await new ChangeRequestRepository(db).GetAsync(fixture.RequestId,
            ChangeRequestLoadShape.None, CancellationToken.None);

        Assert.NotNull(request);
        Assert.Equal(fixture.RequestId, request!.Id);
        Assert.Empty(request.RequirementChanges);
        Assert.Empty(request.ReviewCycles);
        Assert.Empty(request.AuditEvents);
        Assert.Empty(request.UpstreamLinks);
        Assert.Empty(request.UpstreamHistory);
        Assert.Single(measurement.Statements);
        Assert.Equal(1, measurement.Rows);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Legacy_overload_still_loads_comments_for_existing_aggregate_callers()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await using var db = new AeroLinkDbContext(fixture.Options());

        var request = await new ChangeRequestRepository(db).GetAsync(fixture.RequestId, CancellationToken.None);

        Assert.NotNull(request);
        Assert.NotEmpty(request!.ReviewCycles.Single().Comments);
        Assert.NotEmpty(request.RequirementChanges);
        Assert.NotEmpty(request.AuditEvents);
        Assert.NotEmpty(request.UpstreamLinks);
        Assert.NotEmpty(request.UpstreamHistory);
    }

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AeroLinkDbContext> _options;

        private RepositoryFixture(SqliteConnection connection, DbContextOptions<AeroLinkDbContext> options,
            Guid requestId)
        {
            _connection = connection;
            _options = options;
            RequestId = requestId;
        }

        public Guid RequestId { get; }

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("CQ09 load contract program", $"L{Guid.NewGuid():N}"[..6]);
            var project = new ProjectRecord(program.Id, "CQ09 load contract project", "Repository qualification");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var source = new SystemChangeRequest("SRCR-97201", 0, project.Id, release.Id,
                "Load source", "Problem", "Analysis", "Solution", "author", now);
            var request = new SystemChangeRequest("HLRCR-97202", 0, project.Id, release.Id,
                "Load target", "Problem", "Analysis", "Solution", "author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            request.AddRequirementChange("author", "HLR-972001", 0, RequirementLevel.HighLevel,
                RequirementChangeKind.Introduce, "The target shall retain controlled history.", "CQ09 fixture",
                "Test", now, attributesJson: "{\"derived\":true}");
            request.AddUpstreamLink("author", source.Id, source.DisplayNumber, release.Id, release.Version,
                "The source controls the target fixture.", now);
            request.SubmitForReview("author", [new("reviewer", "CQ09 Reviewer")], now);
            request.AddReviewComment("reviewer", ReviewCommentAnchor.ChangeCase, null,
                "The review discussion remains attributable.", now.AddMinutes(1));
            db.AddRange(program, project, release, source, request);
            await db.SaveChangesAsync();
            return new RepositoryFixture(connection, options, request.Id);
        }

        public DbContextOptions<AeroLinkDbContext> Options(QueryReadMeasurement? measurement = null)
        {
            var builder = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(_connection);
            if (measurement is not null) builder.AddInterceptors(measurement);
            return builder.Options;
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
