using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A tracker has no authority over a controlled record, so what is asserted here is that pushing to one
/// cannot duplicate work, cannot lose the reason a push failed, and above all cannot block a change request
/// when the tracker is down.
/// </summary>
public sealed class JiraConnectorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed class FakeJira : IJiraClient
    {
        public List<(string Summary, string Description)> Created { get; } = [];
        public string? StatusToReturn { get; set; } = "In Progress";
        public Exception? Throw { get; set; }
        public JiraPushResult? Refusal { get; set; }
        public bool ProbeReachable { get; set; } = true;

        public Task<JiraProbeResult> ProbeAsync(JiraConnection connection, string apiToken, CancellationToken ct) =>
            Task.FromResult(new JiraProbeResult(ProbeReachable,
                ProbeReachable ? "Reachable." : "The tracker rejected the credentials."));

        public Task<JiraPushResult> CreateIssueAsync(JiraConnection connection, string apiToken, string summary,
            string description, CancellationToken ct)
        {
            if (Throw is not null) return Task.FromException<JiraPushResult>(Throw);
            if (Refusal is not null) return Task.FromResult(Refusal);
            Created.Add((summary, description));
            var key = $"{connection.ProjectKey}-{Created.Count}";
            return Task.FromResult(new JiraPushResult(true, key, $"{connection.BaseUrl}/browse/{key}", $"Created {key}."));
        }

        public Task<string?> ReadStatusAsync(JiraConnection connection, string apiToken, string issueKey, CancellationToken ct) =>
            Task.FromResult(StatusToReturn);
    }

    private static async Task<(DbContextOptions<AeroLinkDbContext> Options, Guid ProjectId, string Path)> SeedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-jira-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Jira Program", "JRA");
        var project = new ProjectRecord(program.Id, "Software", "Jira Software");
        db.AddRange(program, project);
        await db.SaveChangesAsync();
        return (options, project.Id, path);
    }

    private static JiraConnectorService Service(AeroLinkDbContext db, IJiraClient client, string? baseUrl = "https://aerolink.example.test")
    {
        var settings = new Dictionary<string, string?>();
        if (baseUrl is not null) settings["Notifications:BaseUrl"] = baseUrl;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new JiraConnectorService(db, client, DataProtectionProvider.Create("AeroLink.Tests"), configuration);
    }

    private static SystemChangeRequest Scr(Guid projectId) =>
        new("SCR-00031", 0, projectId, Guid.NewGuid(), "Oceanic routing",
            "Sequencing drifts on long oceanic legs.", "The route mode was analyzed.",
            "Correct the sequencing rule.", "author", Now);

    private static async Task<JiraConnection> ConnectAsync(AeroLinkDbContext db, JiraConnectorService service, Guid projectId)
    {
        var connection = new JiraConnection(projectId, "https://jira.example.test", "fms", "Task",
            "engineer@example.test", service.Protect("token-value"), "config.manager", Now);
        db.JiraConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    [Fact]
    public void A_connection_normalizes_what_the_tracker_is_strict_about()
    {
        var connection = new JiraConnection(Guid.NewGuid(), "https://jira.example.test/", "fms", "Task",
            "engineer@example.test", "protected", "config.manager", Now);
        // Jira rejects a lowercase key, and a connection that fails on first use is worse than one that
        // refuses to be created.
        Assert.Equal("FMS", connection.ProjectKey);
        Assert.Equal("https://jira.example.test", connection.BaseUrl);
    }

    [Theory]
    [InlineData("not-a-url", "absolute")]
    [InlineData("https://jira.example.test", "project key")]
    public void A_connection_that_could_not_work_is_refused_when_it_is_made(string baseUrl, string expected)
    {
        var key = expected == "project key" ? "" : "FMS";
        var error = Assert.Throws<DomainException>(() => new JiraConnection(Guid.NewGuid(), baseUrl, key, "Task",
            "engineer@example.test", "protected", "config.manager", Now));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Pushing_a_change_request_creates_one_issue_and_records_where_it_landed()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var jira = new FakeJira();
            var service = Service(db, jira);
            await ConnectAsync(db, service, seed.ProjectId);
            var scr = Scr(seed.ProjectId);
            db.SystemChangeRequests.Add(scr);
            await db.SaveChangesAsync();

            var link = await service.PushChangeRequestAsync(scr, "engineer", Now, default);

            Assert.Equal(JiraLinkState.Linked, link.State);
            Assert.Equal("FMS-1", link.IssueKey);
            Assert.Equal("https://jira.example.test/browse/FMS-1", link.IssueUrl);
            var (summary, description) = Assert.Single(jira.Created);
            Assert.Contains("SCR-00031.00", summary);
            // The point of the link is that somebody reading the tracker can reach the record that is
            // authoritative — and the tracker is never that record.
            Assert.Contains($"https://aerolink.example.test/open/scr/{scr.Id}", description);
            Assert.Contains("Sequencing drifts on long oceanic legs.", description);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Pushing_twice_does_not_create_a_second_issue()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var jira = new FakeJira();
            var service = Service(db, jira);
            await ConnectAsync(db, service, seed.ProjectId);
            var scr = Scr(seed.ProjectId);
            db.SystemChangeRequests.Add(scr);
            await db.SaveChangesAsync();

            var first = await service.PushChangeRequestAsync(scr, "engineer", Now, default);
            var second = await service.PushChangeRequestAsync(scr, "engineer", Now.AddMinutes(1), default);

            // Two issues for one change request is worse than none: the board then disagrees with itself.
            Assert.Equal(first.Id, second.Id);
            Assert.Single(jira.Created);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task An_unreachable_tracker_records_the_reason_and_fails_nothing_else()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var jira = new FakeJira { Throw = new HttpRequestException("Connection refused.") };
            var service = Service(db, jira);
            await ConnectAsync(db, service, seed.ProjectId);
            var scr = Scr(seed.ProjectId);
            db.SystemChangeRequests.Add(scr);
            await db.SaveChangesAsync();

            // A change request must never be blocked by a system that has no authority over it.
            var link = await service.PushChangeRequestAsync(scr, "engineer", Now, default);

            Assert.Equal(JiraLinkState.Failed, link.State);
            Assert.Contains("Connection refused", link.LastError);
            Assert.Equal(ScrState.Draft, (await db.SystemChangeRequests.AsNoTracking().SingleAsync()).State);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_refused_push_can_be_retried_once_the_tracker_is_fixed()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var jira = new FakeJira { Refusal = new JiraPushResult(false, "", "", "The tracker rejected the credentials.") };
            var service = Service(db, jira);
            await ConnectAsync(db, service, seed.ProjectId);
            var scr = Scr(seed.ProjectId);
            db.SystemChangeRequests.Add(scr);
            await db.SaveChangesAsync();

            var failed = await service.PushChangeRequestAsync(scr, "engineer", Now, default);
            Assert.Equal(JiraLinkState.Failed, failed.State);

            // The failed link is kept, not deleted, so somebody can see a push was attempted and why it did
            // not land — and so fixing the credential does not require starting over.
            jira.Refusal = null;
            var retried = await service.PushChangeRequestAsync(scr, "engineer", Now.AddMinutes(5), default);
            Assert.Equal(JiraLinkState.Linked, retried.State);
            Assert.Null(retried.LastError);
            Assert.Single(await db.JiraIssueLinks.AsNoTracking().ToListAsync());
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Status_is_reflected_as_the_tracker_words_it()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var jira = new FakeJira { StatusToReturn = "In Review" };
            var service = Service(db, jira);
            await ConnectAsync(db, service, seed.ProjectId);
            var scr = Scr(seed.ProjectId);
            db.SystemChangeRequests.Add(scr);
            await db.SaveChangesAsync();
            await service.PushChangeRequestAsync(scr, "engineer", Now, default);

            Assert.Equal(1, await service.RefreshStatusesAsync(seed.ProjectId, Now, default));
            var link = await db.JiraIssueLinks.AsNoTracking().SingleAsync();
            // Reflected verbatim. Mapping it onto an AeroLink state would invent a correspondence no two
            // Jira projects agree on.
            Assert.Equal("In Review", link.IssueStatus);
            Assert.NotNull(link.StatusReadAt);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_tracker_that_cannot_be_read_leaves_the_last_known_status_in_place()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var jira = new FakeJira { StatusToReturn = "In Review" };
            var service = Service(db, jira);
            await ConnectAsync(db, service, seed.ProjectId);
            var scr = Scr(seed.ProjectId);
            db.SystemChangeRequests.Add(scr);
            await db.SaveChangesAsync();
            await service.PushChangeRequestAsync(scr, "engineer", Now, default);
            await service.RefreshStatusesAsync(seed.ProjectId, Now, default);

            // A stale status is information. An empty one is not.
            jira.StatusToReturn = null;
            Assert.Equal(0, await service.RefreshStatusesAsync(seed.ProjectId, Now.AddHours(1), default));
            Assert.Equal("In Review", (await db.JiraIssueLinks.AsNoTracking().SingleAsync()).IssueStatus);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_project_with_no_connection_is_told_so_rather_than_failing_obscurely()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var service = Service(db, new FakeJira());
            var scr = Scr(seed.ProjectId);
            db.SystemChangeRequests.Add(scr);
            await db.SaveChangesAsync();

            var error = await Assert.ThrowsAsync<DomainException>(
                () => service.PushChangeRequestAsync(scr, "engineer", Now, default));
            Assert.Contains("no enabled Jira connection", error.Message);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Verifying_records_whether_the_tracker_answered()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var jira = new FakeJira { ProbeReachable = false };
            var service = Service(db, jira);
            var connection = await ConnectAsync(db, service, seed.ProjectId);

            // A broken connection should be visible before somebody needs it, not discovered on their push.
            await service.VerifyAsync(connection, Now, default);
            Assert.Contains("rejected the credentials", connection.LastError);
            Assert.Null(connection.LastVerifiedAt);

            jira.ProbeReachable = true;
            await service.VerifyAsync(connection, Now.AddMinutes(1), default);
            Assert.Null(connection.LastError);
            Assert.Equal(Now.AddMinutes(1), connection.LastVerifiedAt);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Without_a_public_address_the_issue_explains_itself_instead_of_carrying_a_broken_link()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var jira = new FakeJira();
            var service = Service(db, jira, baseUrl: null);
            await ConnectAsync(db, service, seed.ProjectId);
            var scr = Scr(seed.ProjectId);
            db.SystemChangeRequests.Add(scr);
            await db.SaveChangesAsync();

            await service.PushChangeRequestAsync(scr, "engineer", Now, default);
            var (_, description) = Assert.Single(jira.Created);
            Assert.DoesNotContain("http", description);
            Assert.Contains("No public address is", description);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public void A_linked_artifact_cannot_be_pushed_again_by_resetting_its_link()
    {
        var link = new JiraIssueLink(Guid.NewGuid(), Guid.NewGuid(), "ChangeRequest", Guid.NewGuid(), "SCR-00001.00", "engineer", Now);
        link.RecordIssue("FMS-9", "https://jira.example.test/browse/FMS-9", Now);
        Assert.Throws<DomainException>(() => link.Retry(Now.AddMinutes(1)));
    }
}
