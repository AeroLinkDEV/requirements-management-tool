using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AeroLink.Api.Tests;

public sealed class ReleasedSyntheticSourceSupplementApiTests
{
    private static readonly string Sha = new('a', 40);

    [Fact]
    public async Task Preview_apply_read_and_replay_preserve_ordinary_source_history()
    {
        using var transport = new Transport(_ => Task.FromResult(Commit()));
        using var factory = new AeroLinkApiFactory();
        using var app = Configure(factory, transport);
        var data = await SeedAsync(app.Services);
        using var client = app.CreateClient();
        await MemberSession.SignInForReadsAsync(client, data.UserName);
        var route = Route(data);
        var preview = await client.PostAsJsonAsync($"/api/projects/{data.Manifest.ProjectId}/code/source/released-supplement/preview", data.Manifest);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().ReleasedSyntheticSourceSupplements.ToListAsync());
        var applied = await client.PostAsJsonAsync($"/api/projects/{data.Manifest.ProjectId}/code/source/released-supplement/apply", data.Manifest);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var first = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(first.GetProperty("isReplay").GetBoolean());
        var replay = await client.PostAsJsonAsync($"/api/projects/{data.Manifest.ProjectId}/code/source/released-supplement/apply", data.Manifest);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var repeated = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(repeated.GetProperty("isReplay").GetBoolean());
        Assert.Equal(first.GetProperty("supplementId").GetGuid(), repeated.GetProperty("supplementId").GetGuid());
        var read = await client.GetFromJsonAsync<JsonElement>(route + "?releaseId=" + data.Manifest.ReleaseId);
        Assert.Equal(Sha, read.GetProperty("source").GetProperty("commitSha").GetString());
        Assert.True(read.GetProperty("provenance").GetProperty("recordedAfterRelease").GetBoolean());
        Assert.False(read.GetProperty("provenance").GetProperty("partOfOriginalReleasePackage").GetBoolean());
        Assert.False(read.GetProperty("provenance").GetProperty("provesDeliveredBinary").GetBoolean());
        var ordinary = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{data.Manifest.ProjectId}/code/source?releaseId={data.Manifest.ReleaseId}");
        Assert.Equal(0, ordinary.GetProperty("version").GetInt64());
        Assert.Equal(JsonValueKind.Null, ordinary.GetProperty("selectionEventId").ValueKind);
        Assert.Equal(JsonValueKind.Null, ordinary.GetProperty("snapshot").ValueKind);
        Assert.False(ordinary.GetProperty("capabilities").GetProperty("canSelect").GetBoolean());
        var deniedSelection = await client.PostAsJsonAsync($"/api/projects/{data.Manifest.ProjectId}/code/source",
            new { releaseId = data.Manifest.ReleaseId, reference = Sha, referenceKind = "Commit",
                previewSha = Sha, expectedConfigurationVersion = 2, expectedSelectionVersion = 0 });
        Assert.Equal(HttpStatusCode.Conflict, deniedSelection.StatusCode);
        transport.ReturnTree = true;
        var snapshotId = first.GetProperty("sourceSnapshotId").GetGuid();
        var tree = await client.GetAsync($"/api/projects/{data.Manifest.ProjectId}/code/source/{snapshotId}/tree?commit={Sha}");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        using var check = app.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Single(await db.ReleasedSyntheticSourceSupplements.ToListAsync());
        Assert.Empty(await db.GitLabCurrentSourceSelections.ToListAsync());
        Assert.Empty(await db.GitLabSourceSelectionEvents.ToListAsync());
    }

    [Theory]
    [InlineData("role")]
    [InlineData("scope")]
    [InlineData("relationship")]
    public async Task Invalid_authority_or_relationship_intent_is_refused_before_remote_access(string refusal)
    {
        using var transport = new Transport(_ => throw new InvalidOperationException("Remote access must not occur."));
        using var factory = new AeroLinkApiFactory();
        using var app = Configure(factory, transport);
        var data = await SeedAsync(app.Services, refusal == "role" ? ProgramRole.Engineer : ProgramRole.ConfigurationManager);
        if (refusal == "scope")
            app.Services.GetRequiredService<IOptions<ProjectGitLabOptions>>().Value.ReleasedSyntheticSourceSupplementScope = new();
        var body = JsonSerializer.SerializeToNode(data.Manifest)!;
        if (refusal == "relationship") body["relationships"] = new JsonArray();
        using var client = app.CreateClient();
        await MemberSession.SignInForReadsAsync(client, data.UserName);
        foreach (var operation in new[] { "/preview", "/apply" })
        {
            var response = await client.PostAsJsonAsync(Route(data) + operation, body);
            Assert.Equal(refusal == "role" ? HttpStatusCode.Forbidden :
                refusal == "scope" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("membership")]
    [InlineData("repository")]
    [InlineData("preview-role")]
    public async Task Authority_lost_during_remote_wait_prevents_append(string change)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new Transport(async ct => { entered.TrySetResult(); await resume.Task.WaitAsync(ct); return Commit(); });
        using var factory = new AeroLinkApiFactory();
        using var app = Configure(factory, transport);
        var data = await SeedAsync(app.Services);
        using var client = app.CreateClient();
        await MemberSession.SignInForReadsAsync(client, data.UserName);
        var pending = client.PostAsJsonAsync(Route(data) + (change == "preview-role" ? "/preview" : "/apply"), data.Manifest);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            if (change == "session")
                foreach (var session in await db.UserSessions.Where(x => x.UserId == data.UserId).ToListAsync())
                    session.Revoke(DateTimeOffset.UtcNow);
            else if (change == "preview-role")
            {
                (await db.ProjectLeadershipAssignments.SingleAsync(x => x.HolderUserId == data.UserId)).End("tester", DateTimeOffset.UtcNow);
            }
            else if (change == "membership")
                (await db.ProgramMemberships.SingleAsync(x => x.UserId == data.UserId)).End("tester", DateTimeOffset.UtcNow);
            else
                (await db.ProjectRepositoryConfigurations.SingleAsync(x => x.ProjectId == data.Manifest.ProjectId))
                    .Configure(2, ProjectRepositorySetupMode.ConnectNow, "GitLab", "https://gitlab.example/group/replacement", "tester", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        finally { resume.TrySetResult(); }
        using var response = await pending;
        Assert.Equal(change == "repository" ? HttpStatusCode.Conflict :
            (change == "membership" || change == "preview-role") ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        using var check = app.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await checkDb.ReleasedSyntheticSourceSupplements.ToListAsync());
        Assert.Empty(await checkDb.GitLabSourceSnapshots.ToListAsync());
    }

    private static string Route(Data data) => $"/api/projects/{data.Manifest.ProjectId}/code/source/released-supplement";
    private static HttpResponseMessage Commit() => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"" + Sha + "\"}") };
    private static WebApplicationFactory<Program> Configure(AeroLinkApiFactory factory, Transport transport) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<ProjectGitLabOptions>(options => { options.BaseUrl = "https://gitlab.example"; options.ReadAccessToken = "test-only-token"; });
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
    private static async Task<Data> SeedAsync(IServiceProvider services, ProgramRole role = ProgramRole.ConfigurationManager)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var program = new ProgramRecord("Flight Management System Live", "FMSLIVE");
        var project = new ProjectRecord(program.Id, "FMS Product Development", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.5", false);
        var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow, "GitLab", "https://gitlab.example/group/project", "tester", now);
        repository.RecordVerification("tester", now, 17, "group/project");
        var baseline = new CandidateBaseline("SW-01.50", 0, project.Id, release.Id, null, "Synthetic released baseline", "tester", now);
        baseline.FreezeForInception("tester", now);
        baseline.MarkRequirementsMaterialized("tester", new string('c', 64), 0, now);
        var build = new SoftwareBuild(project.Id, release.Id, baseline.Id, "FMS-1.5.0", "Synthetic released build", "tester", now);
        build.MarkReleased(now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Synthetic release", "tester", now);
        campaign.SelectVerificationBuild(build.Id, "tester", now);
        campaign.StartVerification("tester", now);
        campaign.BeginReleaseReview("tester", [("approver", "Approver")], new string('d', 64), now);
        campaign.Approve("approver", now);
        campaign.Release(build.Id, new string('d', 64), "tester", now);
        release.MarkReleased(now);
        baseline.MarkReleased("tester", now);
        var user = new UserAccount("supplement." + tag, "Synthetic test operator", tag + "@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release, repository, baseline, build, campaign, user,
            new ProgramMembership(user.Id, program.Id, role, "tester", now),
            new ShowcaseUpgradeStep(program.Id, "released-campaign", "Synthetic test fixture.", now));
        if (role == ProgramRole.ConfigurationManager)
            db.Add(new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager, user.Id, "tester", now));
        await db.SaveChangesAsync();
        services.GetRequiredService<IOptions<ProjectGitLabOptions>>().Value.ReleasedSyntheticSourceSupplementScope = new()
        {
            ProgramId = program.Id.ToString(), ProjectId = project.Id.ToString(), ReleaseId = release.Id.ToString(),
            BaselineId = baseline.Id.ToString(), CampaignId = campaign.Id.ToString(),
        };
        return new(user.Id, user.UserName, new(1, project.Id, release.Id, campaign.Id, baseline.Id, repository.Id,
            repository.Version, 0, "https://gitlab.example", 17, "group/project", Sha, Sha, GitLabReferenceKind.Commit,
            Guid.NewGuid(), ReleasedSyntheticSourceSupplementService.PolicyId, ReleasedSyntheticSourceSupplementService.AuthorizationReference,
            "Dated source-only supplement for the synthetic released FMS 1.5 demonstration."));
    }
    private sealed record Data(Guid UserId, string UserName, ReleasedSyntheticSourceSupplementManifest Manifest);
    private sealed class Transport(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool ReturnTree { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                {
            Assert.Equal(HttpMethod.Get, request.Method);
            Calls++;
            if (ReturnTree)
            {
                Assert.Contains(Sha, request.RequestUri!.Query);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
            }
            return respond(ct);
        }
    }
}
