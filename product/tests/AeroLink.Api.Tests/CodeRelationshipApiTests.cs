using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class CodeRelationshipApiTests
{
    [Fact]
    public async Task Merge_request_command_requires_exact_typed_target()
    {
        using var transport = new Remote(_ => new(HttpStatusCode.ServiceUnavailable));
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services);
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);

        using var response = await client.PostAsJsonAsync(
            $"/api/projects/{data.ProjectId}/code/relationships/merge-requests", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Register_keeps_recorded_identity_when_remote_metadata_is_unavailable()
    {
        using var transport = new Remote(_ => new(HttpStatusCode.ServiceUnavailable));
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var target = CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 1, "SCR-1.01");
            var row = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17, 12,
                1200, null, null, "group/project", "https://gitlab.example/group/project/-/merge_requests/12",
                "Recorded context", target, CodeRelationshipMeaning.RelatedContext, data.UserName, DateTimeOffset.UtcNow);
            db.GitLabMergeRequestRelationships.Add(row);
            db.GitLabCodeRelationshipEvents.Add(new(CodeRelationshipKind.MergeRequest, row.Id,
                CodeRelationshipEventKind.Added, data.UserName, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var response = await client.GetAsync($"/api/projects/{data.ProjectId}/code/merge-requests/register?releaseId={data.ReleaseId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(12, item.GetProperty("mergeRequestIid").GetInt32());
        Assert.False(item.GetProperty("metadataKnown").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("metadata").ValueKind);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Register_does_not_apply_current_metadata_to_a_colliding_recorded_identity()
    {
        using var transport = new Remote(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"id\":107,\"project_id\":17,\"iid\":7,\"title\":\"Current metadata\",\"state\":\"opened\",\"draft\":false,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/7\"}]")
        });
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var currentTarget = CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 1, "SCR-1.01");
            var oldTarget = CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 1, "SCR-1.02");
            var current = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17, 7,
                107, null, null, "group/project", "https://gitlab.example/group/project/-/merge_requests/7",
                "Recorded current context", currentTarget, CodeRelationshipMeaning.RelatedContext, data.UserName, DateTimeOffset.UtcNow);
            var old = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, "https://old.gitlab.example", 99, 7,
                9907, null, null, "old/project", "https://old.gitlab.example/old/project/-/merge_requests/7",
                "Recorded old context", oldTarget, CodeRelationshipMeaning.RelatedContext, data.UserName, DateTimeOffset.UtcNow.AddSeconds(-1));
            db.AddRange(current, old,
                new GitLabCodeRelationshipEvent(CodeRelationshipKind.MergeRequest, current.Id, CodeRelationshipEventKind.Added, data.UserName, current.RecordedAt),
                new GitLabCodeRelationshipEvent(CodeRelationshipKind.MergeRequest, old.Id, CodeRelationshipEventKind.Added, data.UserName, old.RecordedAt));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var response = await client.GetAsync($"/api/projects/{data.ProjectId}/code/merge-requests/register?releaseId={data.ReleaseId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        var currentItem = Assert.Single(items, x => x.GetProperty("remoteProjectId").GetInt64() == 17);
        Assert.True(currentItem.GetProperty("metadataKnown").GetBoolean());
        Assert.Equal("Current metadata", currentItem.GetProperty("metadata").GetProperty("title").GetString());
        var oldItem = Assert.Single(items, x => x.GetProperty("remoteProjectId").GetInt64() == 99);
        Assert.False(oldItem.GetProperty("metadataKnown").GetBoolean());
        Assert.Equal(JsonValueKind.Null, oldItem.GetProperty("metadata").ValueKind);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Withdraw_and_readd_use_expected_versions_and_append_transition_events()
    {
        using var factory = Configure(new AeroLinkApiFactory(), new Remote(_ => new(HttpStatusCode.ServiceUnavailable)));
        var data = await SeedAsync(factory.Services);
        Guid relationshipId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var targetRecord = new SystemChangeRequest("SRCR-00001", 0,
                data.ProjectId, data.ReleaseId, "Re-add target", "Problem", "Analysis", "Solution", data.UserName, DateTimeOffset.UtcNow);
            var target = CodeRelationshipTarget.ForChangeRequestRevision(targetRecord.Id, targetRecord.Revision, targetRecord.DisplayNumber);
            var row = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17, 12,
                1200, null, null, "group/project", "https://gitlab.example/group/project/-/merge_requests/12",
                "Recorded context", target, CodeRelationshipMeaning.RelatedContext, data.UserName, DateTimeOffset.UtcNow);
            relationshipId = row.Id;
            db.Add(targetRecord);
            db.GitLabMergeRequestRelationships.Add(row);
            db.GitLabCodeRelationshipEvents.Add(new(CodeRelationshipKind.MergeRequest, row.Id,
                CodeRelationshipEventKind.Added, data.UserName, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        const string relationshipKind = "merge-request";
        using var withdrawn = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/relationships/{relationshipKind}/{relationshipId}/withdraw", new { expectedVersion = 1L, rationale = "Context no longer applies." });
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        using var readded = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/relationships/{relationshipKind}/{relationshipId}/re-add", new { expectedVersion = 2L });
        Assert.Equal(HttpStatusCode.OK, readded.StatusCode);
        using var listed = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?releaseId={data.ReleaseId}");
        Assert.True(listed.IsSuccessStatusCode, await listed.Content.ReadAsStringAsync());
        using var history = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships/merge-request/{relationshipId}/history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Equal(3, historyJson.RootElement.GetProperty("events").GetArrayLength());
        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var saved = await db2.GitLabMergeRequestRelationships.SingleAsync(x => x.Id == relationshipId);
        Assert.True(saved.IsActive);
        Assert.Equal(3, saved.Version);
        var events = await db2.GitLabCodeRelationshipEvents.Where(x => x.RelationshipId == relationshipId).ToListAsync();
        Assert.Equal([CodeRelationshipEventKind.Added, CodeRelationshipEventKind.Withdrawn, CodeRelationshipEventKind.ReAdded],
            events.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Select(x => x.EventKind).ToArray());
    }

    [Fact]
    public async Task Readd_refuses_a_withdrawn_relationship_when_its_exact_target_is_unavailable()
    {
        using var factory = Configure(new AeroLinkApiFactory(), new Remote(_ => new(HttpStatusCode.ServiceUnavailable)));
        var data = await SeedAsync(factory.Services);
        Guid relationshipId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var target = CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 1, "SCR-1.01");
            var row = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17, 13,
                1300, null, null, "group/project", "https://gitlab.example/group/project/-/merge_requests/13",
                "Recorded context", target, CodeRelationshipMeaning.RelatedContext, data.UserName, DateTimeOffset.UtcNow);
            relationshipId = row.Id;
            db.Add(row);
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        const string relationshipKind = "merge-request";
        using var withdrawn = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/relationships/{relationshipKind}/{relationshipId}/withdraw",
            new { expectedVersion = 1L, rationale = "No longer applicable." });
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        using var readded = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/relationships/{relationshipKind}/{relationshipId}/re-add",
            new { expectedVersion = 2L });
        Assert.Equal(HttpStatusCode.Conflict, readded.StatusCode);
        using var scope2 = factory.Services.CreateScope();
        var saved = await scope2.ServiceProvider.GetRequiredService<AeroLinkDbContext>().GitLabMergeRequestRelationships
            .SingleAsync(x => x.Id == relationshipId);
        Assert.False(saved.IsActive);
        Assert.Equal(2, saved.Version);
    }

    [Fact]
    public async Task A_released_release_rejects_relationship_withdrawal()
    {
        using var factory = Configure(new AeroLinkApiFactory(), new Remote(_ => new(HttpStatusCode.ServiceUnavailable)));
        var data = await SeedAsync(factory.Services, releaseIsReleased: true);
        Guid relationshipId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var target = CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 1, "SCR-1.01");
            var row = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17, 12,
                1200, null, null, "group/project", "https://gitlab.example/group/project/-/merge_requests/12",
                "Recorded context", target, CodeRelationshipMeaning.RelatedContext, data.UserName, DateTimeOffset.UtcNow);
            relationshipId = row.Id;
            db.GitLabMergeRequestRelationships.Add(row);
            db.GitLabCodeRelationshipEvents.Add(new(CodeRelationshipKind.MergeRequest, row.Id,
                CodeRelationshipEventKind.Added, data.UserName, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        const string relationshipKind = "merge-request";
        using var response = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/relationships/{relationshipKind}/{relationshipId}/withdraw",
            new { expectedVersion = 1L, rationale = "No longer applicable." });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task File_relationship_requires_the_exact_blob_page_and_duplicate_retry_is_idempotent()
    {
        var sha = new string('b', 40);
        var returnUnsupportedType = false;
        using var transport = new Remote(request => request.RequestUri!.AbsolutePath.EndsWith("/repository/tree", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":\"" + sha + "\",\"name\":\"Program.cs\",\"path\":\"src/Program.cs\",\"type\":\"" + (returnUnsupportedType ? "tree" : "blob") + "\",\"mode\":\"100644\"}]") }
            : new(HttpStatusCode.ServiceUnavailable));
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedFileAsync(factory.Services, sha);
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var sourceHistory = await client.GetAsync($"/api/projects/{data.ProjectId}/code/source/history?releaseId={data.ReleaseId}");
        Assert.Equal(HttpStatusCode.OK, sourceHistory.StatusCode);
        using var sourceJson = JsonDocument.Parse(await sourceHistory.Content.ReadAsStringAsync());
        Assert.Single(sourceJson.RootElement.GetProperty("items").EnumerateArray());
        var request = new
        {
            releaseId = data.ReleaseId, sourceSnapshotId = data.SnapshotId, sourceSelectionEventId = (Guid?)null,
            commitSha = sha, path = "src/Program.cs", parentPath = "src", pageSize = 20,
            targetKind = "ChangeRequestRevision", targetId = data.TargetId, meaning = "Implements",
            expectedConfigurationVersion = 2L
        };
        using var first = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/relationships/files", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.True(firstJson.RootElement.GetProperty("changed").GetBoolean());
        using var retry = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/relationships/files", request);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var retryJson = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.False(retryJson.RootElement.GetProperty("changed").GetBoolean());
        returnUnsupportedType = true;
        using var unsupported = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/relationships/files", request);
        Assert.Equal(HttpStatusCode.Conflict, unsupported.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(1, await db.GitLabFileRelationships.CountAsync(x => x.ProjectId == data.ProjectId));
        Assert.Equal(1, await db.GitLabCodeRelationshipEvents.CountAsync(x => x.RelationshipKind == CodeRelationshipKind.File));
    }

    [Fact]
    public async Task Relationship_pages_use_one_global_recorded_at_order()
    {
        const string sha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using var factory = Configure(new AeroLinkApiFactory(), new Remote(_ => new(HttpStatusCode.ServiceUnavailable)));
        var data = await SeedFileAsync(factory.Services, sha);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var target = CodeRelationshipTarget.ForChangeRequestRevision(data.TargetId, 0, "SRCR-1");
            var file = new GitLabFileRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17,
                data.SnapshotId, null, sha, "src/Program.cs", null, null, null, target,
                CodeRelationshipMeaning.Implements, data.UserName, DateTimeOffset.UtcNow);
            var merge = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17, 14,
                1400, null, null, "group/project", "https://gitlab.example/group/project/-/merge_requests/14",
                "Later context", target, CodeRelationshipMeaning.RelatedContext, data.UserName, DateTimeOffset.UtcNow.AddMinutes(1));
            db.AddRange(file, merge);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var first = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?releaseId={data.ReleaseId}&page=1&pageSize=1");
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal("MergeRequest", firstJson.RootElement.GetProperty("items")[0].GetProperty("relationshipKind").GetString());
        using var second = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?releaseId={data.ReleaseId}&page=2&pageSize=1");
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("File", secondJson.RootElement.GetProperty("items")[0].GetProperty("relationshipKind").GetString());
    }

    [Fact]
    public async Task Relationship_read_hides_mutation_capabilities_from_a_view_only_role()
    {
        using var factory = Configure(new AeroLinkApiFactory(), new Remote(_ => new(HttpStatusCode.ServiceUnavailable)));
        var data = await SeedAsync(factory.Services, role: ProgramRole.Reviewer);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var target = CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 1, "SCR-1.01");
            var row = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17, 22,
                2200, null, null, "group/project", "https://gitlab.example/group/project/-/merge_requests/22",
                "Recorded context", target, CodeRelationshipMeaning.RelatedContext, data.UserName, DateTimeOffset.UtcNow);
            db.Add(row);
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var response = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?releaseId={data.ReleaseId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var capabilities = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("capabilities");
        Assert.False(capabilities.GetProperty("canWithdraw").GetBoolean());
        Assert.False(capabilities.GetProperty("canReAdd").GetBoolean());
    }

    [Fact]
    public async Task Relationship_read_hides_mutation_capabilities_for_a_released_build()
    {
        using var factory = Configure(new AeroLinkApiFactory(), new Remote(_ => new(HttpStatusCode.ServiceUnavailable)));
        var data = await SeedAsync(factory.Services, releaseIsReleased: true);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var target = CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 1, "SCR-1.01");
            var row = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17, 23,
                2300, null, null, "group/project", "https://gitlab.example/group/project/-/merge_requests/23",
                "Recorded context", target, CodeRelationshipMeaning.RelatedContext, data.UserName, DateTimeOffset.UtcNow);
            db.Add(row);
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var response = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?releaseId={data.ReleaseId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var capabilities = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("capabilities");
        Assert.False(capabilities.GetProperty("canWithdraw").GetBoolean());
        Assert.False(capabilities.GetProperty("canReAdd").GetBoolean());
    }

    [Fact]
    public async Task Relationship_command_refuses_a_target_from_another_project()
    {
        const string sha = "cccccccccccccccccccccccccccccccccccccccc";
        using var transport = new Remote(request => request.RequestUri!.AbsolutePath.EndsWith("/repository/tree", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":\"" + sha + "\",\"name\":\"Program.cs\",\"path\":\"src/Program.cs\",\"type\":\"blob\",\"mode\":\"100644\"}]") }
            : new(HttpStatusCode.ServiceUnavailable));
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var own = await SeedFileAsync(factory.Services, sha);
        var foreign = await SeedFileAsync(factory.Services, sha);
        using var client = factory.CreateClient();
        await SignInAsync(client, own.UserName);
        var request = new
        {
            releaseId = own.ReleaseId, sourceSnapshotId = own.SnapshotId, sourceSelectionEventId = (Guid?)null,
            commitSha = sha, path = "src/Program.cs", parentPath = "src", pageSize = 20,
            targetKind = "ChangeRequestRevision", targetId = foreign.TargetId, meaning = "Implements",
            expectedConfigurationVersion = 2L
        };
        using var response = await client.PostAsJsonAsync($"/api/projects/{own.ProjectId}/code/relationships/files", request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().GitLabFileRelationships
            .Where(x => x.ProjectId == own.ProjectId).ToListAsync());
    }

    private static WebApplicationFactory<Program> Configure(AeroLinkApiFactory factory, Remote transport) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<ProjectGitLabOptions>(options => { options.BaseUrl = "https://gitlab.example"; options.ReadAccessToken = "test-only-token"; });
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport);
        }));

    private static async Task<(Guid ProjectId, Guid ReleaseId, string UserName)> SeedAsync(IServiceProvider services,
        bool releaseIsReleased = false, ProgramRole role = ProgramRole.Engineer)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Relationship test " + tag, "GR" + tag);
        var project = new ProjectRecord(program.Id, "Relationship project", "Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        if (releaseIsReleased) release.MarkReleased(now);
        var user = new UserAccount("relationship." + tag, "Relationship engineer", tag + "@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/group/project", "test.setup", now);
        repository.RecordVerification("test.setup", now, 17, "group/project");
        db.AddRange(program, project, release, user, repository,
            new ProgramMembership(user.Id, program.Id, role, "test.setup", now));
        await db.SaveChangesAsync();
        return (project.Id, release.Id, user.UserName);
    }

    private static async Task<(Guid ProjectId, Guid ReleaseId, string UserName, Guid SnapshotId, Guid SelectionEventId, Guid TargetId)> SeedFileAsync(
        IServiceProvider services, string sha)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("File relationship test " + tag, "GF" + tag);
        var project = new ProjectRecord(program.Id, "File relationship project", "Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var user = new UserAccount("file.relationship." + tag, "File relationship engineer", tag + "@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/group/project", "test.setup", now);
        repository.RecordVerification("test.setup", now, 17, "group/project");
        var target = new SystemChangeRequest("SRCR-" + Random.Shared.Next(10000, 99999), 0, project.Id, release.Id, "File target",
            "Problem", "Analysis", "Solution", user.UserName, now);
        var snapshot = new GitLabSourceSnapshot(project.Id, repository.Id, "https://gitlab.example", 17,
            "group/project", sha, "main", user.UserName, now, repository.Version);
        var selection = new GitLabSourceSelectionEvent(project.Id, release.Id, snapshot.Id, 0, user.UserName, now);
        var current = new GitLabCurrentSourceSelection(project.Id, release.Id, snapshot.Id, selection.Id, user.UserName, now);
        db.AddRange(program, project, release, user, repository,
            new ProgramMembership(user.Id, program.Id, ProgramRole.Engineer, "test.setup", now), target,
            snapshot, selection, current);
        await db.SaveChangesAsync();
        return (project.Id, release.Id, user.UserName, snapshot.Id, selection.Id, target.Id);
    }

    private static async Task SignInAsync(HttpClient client, string userName) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword })).StatusCode);

    private sealed class Remote(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(respond(request)); }
    }
}
