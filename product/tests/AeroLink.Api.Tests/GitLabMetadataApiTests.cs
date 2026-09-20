using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class GitLabMetadataApiTests
{
    [Fact]
    public async Task Cached_display_preserves_observation_time_and_still_requires_current_membership()
    {
        using var transport = new CaptureGitLab(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));
        using var factory = new AeroLinkApiFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<ProjectGitLabOptions>(options => { options.BaseUrl = "https://gitlab.example"; options.ReadAccessToken = "test-only-token"; });
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
        var data = await SeedAsync(configured.Services);
        using var client = configured.CreateClient();
        await SignInAsync(client, data.UserName);
        var url = $"/api/projects/{data.ProjectId}/repository/merge-requests";
        var first = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(url);
        using var response = await client.GetAsync(url);
        var second = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(first.GetProperty("checkedAt").GetString(), second.GetProperty("checkedAt").GetString());
        Assert.True(second.GetProperty("cache").GetProperty("reused").GetBoolean());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(1, transport.Calls);
        using (var scope = configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            (await db.ProgramMemberships.SingleAsync(x => x.UserId == data.UserId)).End("test.operator", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Source_bound_tree_refuses_repository_drift_even_when_commit_is_shared()
    {
        using var transport = new CaptureGitLab(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));
        using var factory = new AeroLinkApiFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<ProjectGitLabOptions>(options => { options.BaseUrl = "https://gitlab.example"; options.ReadAccessToken = "test-only-token"; });
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
        var data = await SeedAsync(configured.Services);
        var sha = new string('a', 40);
        Guid snapshotId;
        using (var scope = configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var repository = await db.ProjectRepositoryConfigurations.SingleAsync(x => x.ProjectId == data.ProjectId);
            var snapshot = new GitLabSourceSnapshot(data.ProjectId, repository.Id, "https://gitlab.example", 17,
                "group/project", sha, "main", "test.operator", DateTimeOffset.UtcNow, repository.Version);
            snapshotId = snapshot.Id;
            db.Add(snapshot);
            await db.SaveChangesAsync();
        }
        using var client = configured.CreateClient();
        await SignInAsync(client, data.UserName);
        var url = $"/api/projects/{data.ProjectId}/code/source/{snapshotId}/tree?commit={sha}";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(1, transport.Calls);
        using (var scope = configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var repository = await db.ProjectRepositoryConfigurations.SingleAsync(x => x.ProjectId == data.ProjectId);
            repository.Configure(repository.Version, ProjectRepositorySetupMode.ConnectNow, "GitLab", "https://gitlab.example/group/replacement", "test.operator", DateTimeOffset.UtcNow);
            repository.RecordVerification("test.operator", DateTimeOffset.UtcNow, 18, "group/replacement");
            await db.SaveChangesAsync();
        }
        using var changed = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains("repository_changed", await changed.Content.ReadAsStringAsync());
        Assert.Equal(1, transport.Calls);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url.Replace(snapshotId.ToString(), Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task UnconfiguredInstallationReportsUnavailableWithoutCallingGitLab()
    {
        using var transport = new CaptureGitLab(_ => throw new InvalidOperationException("Unconfigured remote call"));
        using var factory = new AeroLinkApiFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<ProjectGitLabOptions>(options => { options.BaseUrl = ""; options.ReadAccessToken = ""; });
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
        var data = await SeedAsync(configured.Services);
        using var client = configured.CreateClient();
        await SignInAsync(client, data.UserName);
        using var response = await client.GetAsync($"/api/projects/{data.ProjectId}/repository/merge-requests");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var observation = document.RootElement.GetProperty("observation");
        Assert.Equal("service_unconfigured", observation.GetProperty("code").GetString());
        Assert.False(observation.GetProperty("succeeded").GetBoolean());
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task AuthorizedMembersCanReadDiscoveryDetailsCommitAndPinnedTreeWithoutSourceContent()
    {
        var sha = new string('a', 40);
        var mr = "{\"id\":101,\"project_id\":17,\"iid\":1,\"title\":\"Retain valid state\",\"state\":\"opened\",\"draft\":true,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/1\",\"sha\":\"" + sha + "\",\"merge_commit_sha\":null,\"merged_at\":null}";
        var requests = new List<string>();
        using var transport = new RouteGitLab(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var uri = request.RequestUri!;
            requests.Add(uri.AbsolutePath);
            var body = uri.AbsolutePath switch
            {
                "/api/v4/projects/17/merge_requests" => "[" + mr + "]",
                "/api/v4/projects/17/merge_requests/1" => mr,
                "/api/v4/projects/17/merge_requests/1/approvals" => "{\"approved\":true,\"approved_by\":[]}",
                var resource when resource == "/api/v4/projects/17/repository/commits/" + sha => "{\"id\":\"" + sha + "\"}",
                "/api/v4/projects/17/repository/tree" => "[{\"id\":\"" + sha + "\",\"name\":\"README.md\",\"path\":\"README.md\",\"type\":\"blob\",\"mode\":\"100644\"}]",
                "/api/v4/projects/17/repository/tags/release-demo" => "{\"name\":\"release-demo\",\"commit\":{\"id\":\"" + sha + "\"}}",
                _ => throw new InvalidOperationException("Unexpected remote resource: " + uri.AbsolutePath)
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Headers.TryAddWithoutValidation("X-Next-Page", "");
            return response;
        });
        using var factory = new AeroLinkApiFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<ProjectGitLabOptions>(options =>
            { options.BaseUrl = "https://gitlab.example"; options.ReadAccessToken = "test-only-token"; });
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
        var data = await SeedAsync(configured.Services);
        using var client = configured.CreateClient();
        await SignInAsync(client, data.UserName);
        var routes = new[]
        {
            $"/api/projects/{data.ProjectId}/repository/merge-requests",
            $"/api/projects/{data.ProjectId}/repository/merge-requests/1",
            $"/api/projects/{data.ProjectId}/repository/commit?reference={sha}",
            $"/api/projects/{data.ProjectId}/repository/commit?reference=release-demo&referenceKind=Tag",
            $"/api/projects/{data.ProjectId}/repository/tree?commit={sha}"
        };
        foreach (var route in routes)
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("test-only-token", text);
            using var document = JsonDocument.Parse(text);
            Assert.Equal(17, document.RootElement.GetProperty("remoteProjectId").GetInt64());
            Assert.Equal("ok", document.RootElement.GetProperty("observation").GetProperty("code").GetString());
        }
        Assert.Equal(6, requests.Count);
        Assert.Contains("/api/v4/projects/17/repository/tags/release-demo", requests);
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("account")]
    [InlineData("session")]
    [InlineData("repository")]
    public async Task RemoteMetadataIsWithheldWhenAuthorityChangesDuringTheWait(string change)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new CaptureGitLab(async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        using var factory = new AeroLinkApiFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<ProjectGitLabOptions>(options =>
            { options.BaseUrl = "https://gitlab.example"; options.ReadAccessToken = "test-only-token"; });
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
        var data = await SeedAsync(configured.Services);
        using var client = configured.CreateClient();
        await SignInAsync(client, data.UserName);
        var pending = client.GetAsync($"/api/projects/{data.ProjectId}/repository/merge-requests");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            using var scope = configured.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            if (change == "membership")
                (await db.ProgramMemberships.SingleAsync(x => x.UserId == data.UserId)).End("test.operator", DateTimeOffset.UtcNow);
            else if (change == "account")
                (await db.UserAccounts.SingleAsync(x => x.Id == data.UserId)).Disable(DateTimeOffset.UtcNow);
            else if (change == "session")
                foreach (var session in await db.UserSessions.Where(x => x.UserId == data.UserId).ToListAsync())
                    session.Revoke(DateTimeOffset.UtcNow);
            else
                (await db.ProjectRepositoryConfigurations.SingleAsync(x => x.ProjectId == data.ProjectId))
                    .Configure(2, ProjectRepositorySetupMode.ConnectNow, "GitLab", "https://gitlab.example/group/replacement", "test.operator", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        finally { release.TrySetResult(); }
        using var response = await pending;
        var expected = change == "repository" ? HttpStatusCode.Conflict
            : change == "membership" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, transport.Calls);
        Assert.DoesNotContain("observation", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProjectMembershipIsRequiredBeforeAnyRemoteRequest()
    {
        using var transport = new CaptureGitLab(_ => throw new InvalidOperationException("Unauthorized remote call"));
        using var factory = new AeroLinkApiFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport)));
        var data = await SeedAsync(configured.Services);
        using var client = configured.CreateClient();
        await SignInAsync(client, data.UserName);
        var foreignProject = (await SeedAsync(configured.Services)).ProjectId;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{foreignProject}/repository/merge-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{foreignProject}/repository/merge-requests/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{foreignProject}/repository/commit?reference=main")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{foreignProject}/repository/tree?commit={new string('a', 40)}")).StatusCode);
        Assert.Equal(0, transport.Calls);
    }

    private static async Task<(Guid ProjectId, Guid UserId, string UserName)> SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Metadata test " + tag, "GM" + tag);
        var project = new ProjectRecord(program.Id, "Metadata project", "Software");
        var user = new UserAccount("metadata." + tag, "Metadata member", tag + "@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/group/project", "test.setup", now);
        repository.RecordVerification("test.setup", now, 17, "group/project");
        db.AddRange(program, project, user, repository,
            new ProgramMembership(user.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return (project.Id, user.Id, user.UserName);
    }

    private static async Task SignInAsync(HttpClient client, string userName) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword })).StatusCode);

    private sealed class CaptureGitLab(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return respond(cancellationToken); }
    }

    private sealed class RouteGitLab(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
