using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AeroLink.Api.Tests;

public sealed class GitLabSourceApiTests
{
    private static readonly string Sha = new('a', 40);

    [Fact]
    public async Task Synthetic_demo_classification_requires_exact_operator_binding_and_verified_repository()
    {
        using var transport = new Transport(_ => throw new InvalidOperationException("Classification must not call GitLab"));
        using var factory = new AeroLinkApiFactory();
        using var configured = Configure(factory, transport);
        var data = await SeedAsync(configured.Services, programCode: FmsShowcaseSeeder.ProgramCode);
        using var client = configured.CreateClient();
        await MemberSession.SignInForReadsAsync(client, data.UserName);
        var route = $"/api/projects/{data.ProjectId}/code/source?releaseId={data.ReleaseId}";
        var settings = configured.Services.GetRequiredService<IOptions<ProjectGitLabOptions>>().Value;
        async Task<JsonElement> Read() => await client.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal(JsonValueKind.Null, (await Read()).GetProperty("demonstration").ValueKind);
        settings.SyntheticDemoProjectId = data.ProjectId.ToString();
        settings.SyntheticDemoRemoteProjectId = "17";
        var bound = (await Read()).GetProperty("demonstration");
        Assert.Equal(17, bound.GetProperty("remoteProjectId").GetInt64());
        Assert.Equal(2, bound.GetProperty("configurationVersion").GetInt64());

        settings.SyntheticDemoProjectId = Guid.NewGuid().ToString();
        Assert.Equal(JsonValueKind.Null, (await Read()).GetProperty("demonstration").ValueKind);
        settings.SyntheticDemoProjectId = data.ProjectId.ToString();
        foreach (var remote in new[] { "", "invalid", "0", "18" })
        {
            settings.SyntheticDemoRemoteProjectId = remote;
            Assert.Equal(JsonValueKind.Null, (await Read()).GetProperty("demonstration").ValueKind);
        }
        settings.SyntheticDemoRemoteProjectId = "17";
        settings.BaseUrl = "https://different.example";
        Assert.Equal(JsonValueKind.Null, (await Read()).GetProperty("demonstration").ValueKind);
        settings.BaseUrl = "https://gitlab.example";

        using var scope = configured.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var repository = await db.ProjectRepositoryConfigurations.SingleAsync(x => x.ProjectId == data.ProjectId);
        repository.RecordVerification("tester", DateTimeOffset.UtcNow, 17, "group/project");
        await db.SaveChangesAsync();
        var refreshed = (await Read()).GetProperty("demonstration");
        Assert.Equal(repository.Id, refreshed.GetProperty("configurationId").GetGuid());
        Assert.Equal(3, refreshed.GetProperty("configurationVersion").GetInt64());
        repository.RecordVerification("tester", DateTimeOffset.UtcNow, 17, "group/different");
        await db.SaveChangesAsync();
        Assert.Equal(JsonValueKind.Null, (await Read()).GetProperty("demonstration").ValueKind);
        repository.RecordVerificationFailure("tester", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        Assert.Equal(JsonValueKind.Null, (await Read()).GetProperty("demonstration").ValueKind);
        var ordinary = await SeedAsync(configured.Services);
        settings.SyntheticDemoProjectId = ordinary.ProjectId.ToString();
        Assert.Null(await GitLabSyntheticDemonstration.ReadAsync(db, ordinary.ProjectId, settings, default));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Exact_preview_confirmation_is_persisted_and_stale_confirmation_cannot_overwrite_it()
    {
        using var transport = new Transport(_ => Task.FromResult(Commit()));
        using var factory = new AeroLinkApiFactory();
        using var configured = Configure(factory, transport);
        var data = await SeedAsync(configured.Services);
        using var client = configured.CreateClient();
        await MemberSession.SignInForReadsAsync(client, data.UserName);
        var route = $"/api/projects/{data.ProjectId}/code/source";
        using var accepted = await client.PostAsJsonAsync(route, Request(data.ReleaseId));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var saved = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var eventId = saved.RootElement.GetProperty("selectionEventId").GetGuid();
        using var read = await client.GetAsync(route + "?releaseId=" + data.ReleaseId);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var state = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("demonstration").ValueKind);
        Assert.Equal(eventId, state.RootElement.GetProperty("selectionEventId").GetGuid());
        Assert.Equal(Sha, state.RootElement.GetProperty("snapshot").GetProperty("commitSha").GetString());
        Assert.Equal("https://gitlab.example", state.RootElement.GetProperty("snapshot").GetProperty("instanceBaseUrl").GetString());
        using var stale = await client.PostAsJsonAsync(route, Request(data.ReleaseId));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var scope = configured.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(eventId, (await db.GitLabCurrentSourceSelections.SingleAsync(x => x.ProjectId == data.ProjectId)).SelectionEventId);
        Assert.Equal(1, await db.GitLabSourceSelectionEvents.CountAsync(x => x.ProjectId == data.ProjectId));
    }

    [Fact]
    public async Task Project_access_is_required_before_observation_or_reading_a_source_selection()
    {
        using var transport = new Transport(_ => throw new InvalidOperationException("No remote request permitted"));
        using var factory = new AeroLinkApiFactory();
        using var configured = Configure(factory, transport);
        var own = await SeedAsync(configured.Services);
        var other = await SeedAsync(configured.Services);
        using var client = configured.CreateClient();
        await MemberSession.SignInForReadsAsync(client, own.UserName);
        var route = $"/api/projects/{other.ProjectId}/code/source";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(route, Request(other.ReleaseId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(route + "?releaseId=" + other.ReleaseId)).StatusCode);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Source_read_does_not_advertise_selection_to_a_view_only_member()
    {
        using var transport = new Transport(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var factory = new AeroLinkApiFactory();
        using var configured = Configure(factory, transport);
        var data = await SeedAsync(configured.Services, role: ProgramRole.Reviewer);
        using var client = configured.CreateClient();
        await MemberSession.SignInForReadsAsync(client, data.UserName);

        using var response = await client.GetAsync($"/api/projects/{data.ProjectId}/code/source?releaseId={data.ReleaseId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("capabilities").GetProperty("canSelect").GetBoolean());
    }

    [Theory]
    [InlineData("session")]
    [InlineData("account")]
    [InlineData("membership")]
    [InlineData("repository")]
    public async Task Authority_changes_during_remote_wait_prevent_source_persistence(string change)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new Transport(async ct => { entered.TrySetResult(); await resume.Task.WaitAsync(ct); return Commit(); });
        using var factory = new AeroLinkApiFactory();
        using var configured = Configure(factory, transport);
        var data = await SeedAsync(configured.Services);
        using var client = configured.CreateClient();
        await MemberSession.SignInForReadsAsync(client, data.UserName);
        var pending = client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/source", Request(data.ReleaseId));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            using var scope = configured.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            if (change == "session")
                foreach (var session in await db.UserSessions.Where(x => x.UserId == data.UserId).ToListAsync()) session.Revoke(DateTimeOffset.UtcNow);
            else if (change == "account") (await db.UserAccounts.SingleAsync(x => x.Id == data.UserId)).Disable(DateTimeOffset.UtcNow);
            else if (change == "membership") (await db.ProgramMemberships.SingleAsync(x => x.UserId == data.UserId)).End("tester", DateTimeOffset.UtcNow);
            else (await db.ProjectRepositoryConfigurations.SingleAsync(x => x.ProjectId == data.ProjectId))
                .Configure(2, ProjectRepositorySetupMode.ConnectNow, "GitLab", "https://gitlab.example/group/replacement", "tester", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        finally { resume.TrySetResult(); }
        using var response = await pending;
        Assert.Equal(change == "repository" ? HttpStatusCode.Conflict : change == "membership" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized,
            response.StatusCode);
        using var checkScope = configured.Services.CreateScope();
        Assert.Empty(await checkScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().GitLabSourceSelectionEvents
            .Where(x => x.ProjectId == data.ProjectId).ToListAsync());
    }

    [Fact]
    public async Task Linked_files_are_grouped_before_paging_and_scoped_to_exact_snapshot()
    {
        using var factory = new AeroLinkApiFactory();
        var data = await SeedAsync(factory.Services);
        Guid snapshotId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var configuration = await db.ProjectRepositoryConfigurations.SingleAsync(x => x.ProjectId == data.ProjectId);
            var snapshot = new GitLabSourceSnapshot(data.ProjectId, configuration.Id, "https://gitlab.example", 17,
                "group/project", Sha, "main", "tester", DateTimeOffset.UtcNow, configuration.Version);
            var other = new GitLabSourceSnapshot(data.ProjectId, configuration.Id, "https://gitlab.example", 17,
                "group/project", new string('b', 40), "next", "tester", DateTimeOffset.UtcNow, configuration.Version);
            snapshotId = snapshot.Id;
            db.AddRange(snapshot, other);
            foreach (var path in new[] { "src/a.c", "src/a.c", "src/b.c", "src/c.c" })
                db.Add(new GitLabFileRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17,
                    snapshot.Id, null, Sha, path, null, null, null,
                    CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 0, "LLRCR-00001.00"),
                    CodeRelationshipMeaning.RelatedContext, "fixture", DateTimeOffset.UtcNow));
            db.Add(new GitLabFileRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17,
                other.Id, null, other.CommitSha, "src/other.c", null, null, null,
                CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 0, "LLRCR-00002.00"),
                CodeRelationshipMeaning.RelatedContext, "fixture", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await MemberSession.SignInForReadsAsync(client, data.UserName);
        var url = $"/api/projects/{data.ProjectId}/code/files?releaseId={data.ReleaseId}&sourceSnapshotId={snapshotId}&pageSize=1";
        var first = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(url);
        Assert.Equal(3, first.GetProperty("total").GetInt32());
        var item = Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal("src/a.c", item.GetProperty("path").GetString());
        Assert.Equal(2, item.GetProperty("relationshipCount").GetInt32());
        var second = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(url + "&page=2");
        Assert.Equal("src/b.c", Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("path").GetString());
        var searched = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(url + "&search=c.c");
        Assert.Equal(1, searched.GetProperty("total").GetInt32());
        Assert.Equal("src/c.c", Assert.Single(searched.GetProperty("items").EnumerateArray()).GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url.Replace(snapshotId.ToString(), Guid.NewGuid().ToString()))).StatusCode);
    }

    private static object Request(Guid releaseId) => new { releaseId, reference = Sha, referenceKind = "Commit",
        previewSha = Sha, expectedConfigurationVersion = 2, expectedSelectionVersion = 0 };
    private static HttpResponseMessage Commit() => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"" + Sha + "\"}") };
    private static WebApplicationFactory<Program> Configure(AeroLinkApiFactory factory, Transport transport) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<ProjectGitLabOptions>(options => { options.BaseUrl = "https://gitlab.example"; options.ReadAccessToken = "test-only-token"; });
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
    private static async Task<(Guid ProjectId, Guid ReleaseId, Guid UserId, string UserName)> SeedAsync(
        IServiceProvider services, ProgramRole role = ProgramRole.Engineer, string? programCode = null)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..8]; var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Source test " + tag, programCode ?? "GS" + tag);
        var project = new ProjectRecord(program.Id, "Source project", "Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var user = new UserAccount("source." + tag, "Source engineer", tag + "@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow, "GitLab", "https://gitlab.example/group/project", "tester", now);
        repository.RecordVerification("tester", now, 17, "group/project");
        db.AddRange(program, project, release, user, repository, new ProgramMembership(user.Id, program.Id, role, "tester", now));
        await db.SaveChangesAsync();
        return (project.Id, release.Id, user.Id, user.UserName);
    }
    private sealed class Transport(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Assert.Equal(HttpMethod.Get, request.Method); Calls++; return respond(ct); }
    }
}
