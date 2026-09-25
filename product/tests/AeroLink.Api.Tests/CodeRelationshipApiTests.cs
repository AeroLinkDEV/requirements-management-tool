using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
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
    public async Task Relationship_read_preserves_the_exact_target_owner_identity()
    {
        using var factory = Configure(new AeroLinkApiFactory(), new Remote(_ => new(HttpStatusCode.ServiceUnavailable)));
        var data = await SeedAsync(factory.Services);
        var ownerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var target = CodeRelationshipTarget.ForRequirementProposal(targetId, ownerId,
                "LLR-000001.00 proposal in LLRCR-000002.00");
            db.GitLabMergeRequestRelationships.Add(new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId,
                "https://gitlab.example", 17, 12, 1200, null, null, "group/project",
                "https://gitlab.example/group/project/-/merge_requests/12", "Recorded proposal", target,
                CodeRelationshipMeaning.Addresses, data.UserName, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var response = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?releaseId={data.ReleaseId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(targetId, item.GetProperty("targetIdentityId").GetGuid());
        Assert.Equal(ownerId, item.GetProperty("targetOwnerIdentityId").GetGuid());
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
        var checkedAt = json.RootElement.GetProperty("metadataCheckedAt").GetDateTimeOffset();
        using var reused = await client.GetAsync($"/api/projects/{data.ProjectId}/code/merge-requests/register?releaseId={data.ReleaseId}");
        reused.EnsureSuccessStatusCode();
        using var reusedJson = JsonDocument.Parse(await reused.Content.ReadAsStringAsync());
        Assert.True(reusedJson.RootElement.GetProperty("metadataReused").GetBoolean());
        Assert.Equal(checkedAt, reusedJson.RootElement.GetProperty("metadataCheckedAt").GetDateTimeOffset());
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
    public async Task Relationship_file_filters_bind_snapshot_and_use_exact_ordinal_path_before_paging()
    {
        const string firstSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string secondSha = "cccccccccccccccccccccccccccccccccccccccc";
        using var factory = Configure(new AeroLinkApiFactory(), new Remote(_ => new(HttpStatusCode.ServiceUnavailable)));
        var data = await SeedFileAsync(factory.Services, firstSha);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var repository = await db.ProjectRepositoryConfigurations.SingleAsync(x => x.ProjectId == data.ProjectId);
            var target = await db.SystemChangeRequests.SingleAsync(x => x.Id == data.TargetId);
            var targetSnapshot = CodeRelationshipTarget.ForChangeRequestRevision(target.Id, target.Revision, target.DisplayNumber);
            var firstFile = new GitLabFileRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17,
                data.SnapshotId, data.SelectionEventId, firstSha, "src/Program.cs", null, null, null, targetSnapshot,
                CodeRelationshipMeaning.Implements, data.UserName, now);
            var secondFirstSnapshotFile = new GitLabFileRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17,
                data.SnapshotId, data.SelectionEventId, firstSha, "src/Program.cs", 1, 1, null, targetSnapshot,
                CodeRelationshipMeaning.Implements, data.UserName, now.AddSeconds(1));
            var secondSnapshot = new GitLabSourceSnapshot(data.ProjectId, repository.Id, "https://gitlab.example", 17,
                "group/project", secondSha, "feature", data.UserName, now, repository.Version);
            var secondSelection = new GitLabSourceSelectionEvent(data.ProjectId, data.ReleaseId, secondSnapshot.Id, 1,
                data.UserName, now);
            var secondFile = new GitLabFileRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17,
                secondSnapshot.Id, secondSelection.Id, secondSha, "src/Program.cs", null, null, null, targetSnapshot,
                CodeRelationshipMeaning.Implements, data.UserName, now);
            db.AddRange(firstFile, secondFirstSnapshotFile, secondSnapshot, secondSelection, secondFile,
                new GitLabCodeRelationshipEvent(CodeRelationshipKind.File, firstFile.Id,
                    CodeRelationshipEventKind.Added, data.UserName, now),
                new GitLabCodeRelationshipEvent(CodeRelationshipKind.File, secondFirstSnapshotFile.Id,
                    CodeRelationshipEventKind.Added, data.UserName, now.AddSeconds(1)),
                new GitLabCodeRelationshipEvent(CodeRelationshipKind.File, secondFile.Id,
                    CodeRelationshipEventKind.Added, data.UserName, now));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        var exact = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?relationshipKind=File&sourceSnapshotId={data.SnapshotId}&path=src/Program.cs&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        using var exactJson = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());
        Assert.Equal(2, exactJson.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("src/Program.cs", exactJson.RootElement.GetProperty("items")[0].GetProperty("path").GetString());
        var exactPage2 = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?relationshipKind=File&sourceSnapshotId={data.SnapshotId}&path=src/Program.cs&page=2&pageSize=1");
        using var exactPage2Json = JsonDocument.Parse(await exactPage2.Content.ReadAsStringAsync());
        Assert.Single(exactPage2Json.RootElement.GetProperty("items").EnumerateArray());

        var caseMismatch = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?relationshipKind=File&sourceSnapshotId={data.SnapshotId}&path=src/program.cs");
        Assert.Equal(HttpStatusCode.OK, caseMismatch.StatusCode);
        using var caseJson = JsonDocument.Parse(await caseMismatch.Content.ReadAsStringAsync());
        Assert.Equal(0, caseJson.RootElement.GetProperty("total").GetInt32());

        var mixed = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?relationshipKind=MergeRequest&sourceSnapshotId={data.SnapshotId}&path=src/Program.cs");
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        var foreign = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?relationshipKind=File&sourceSnapshotId={Guid.NewGuid()}&path=src/Program.cs");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Legacy_gitlab_post_is_refused_before_remote_or_legacy_record_write()
    {
        using var transport = new Remote(_ => throw new InvalidOperationException("The retired route must not call GitLab."));
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services);
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var response = await client.PostAsJsonAsync("/api/code-traceability", new
        {
            projectId = data.ProjectId, releaseId = data.ReleaseId, requirementArtifactId = Guid.NewGuid(),
            requirementRevisionId = Guid.NewGuid(), disposition = "GitLabMerge", repositoryPath = "group/project",
            mergeRequestReference = "!1", mergeRequestTitle = "retired", mergeRequestUrl = "https://gitlab.example/group/project/-/merge_requests/1",
            mergeCommitSha = new string('a', 40), mergedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("legacy_gitlab_capture_retired", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Evidence_no_code_validation_rejects_mixed_contributions_without_gitlab()
    {
        using var transport = new Remote(_ => throw new InvalidOperationException("No-code validation must not call GitLab."));
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services);
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var response = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", new
        {
            releaseId = data.ReleaseId, requirementArtifactId = Guid.NewGuid(), requirementRevisionId = Guid.NewGuid(),
            expectedBaselineId = Guid.NewGuid(), disposition = "NoCodeChangeRequired", expectedSelectorVersion = 0L, expectedLegacyRecordId = (Guid?)null,
            contributions = new[] { new { kind = "File", relationshipId = Guid.NewGuid(), expectedRelationshipVersion = 1L } },
            noCodeChangeRationale = "not applicable"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Evidence_endpoint_accepts_no_code_without_gitlab_after_exact_baseline_materialization()
    {
        using var transport = new Remote(_ => throw new InvalidOperationException("No-code acceptance must not call GitLab."));
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services);
        Guid artifactId;
        Guid revisionId;
        Guid baselineId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var baseline = new CandidateBaseline("BL-900001", 0, data.ProjectId, data.ReleaseId, null,
                "Acceptance baseline", data.UserName, now);
            var system = new RequirementArtifact(data.ProjectId, "SYS-900001", RequirementLevel.System, now);
            var high = new RequirementArtifact(data.ProjectId, "HLR-900001", RequirementLevel.HighLevel, now);
            var artifact = new RequirementArtifact(data.ProjectId, "LLR-900001", RequirementLevel.LowLevel, now);
            var change = new SystemChangeRequest("LLRCR-90001", 0, data.ProjectId, data.ReleaseId,
                "No-code acceptance", "Problem", "Analysis", "Solution", data.UserName, now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel);
            var systemRevision = new RequirementRevision(system.Id, 1, "System behavior.", "Acceptance test", "Test",
                RequirementRevisionState.Active, change.Id, baseline.Id, now);
            var highRevision = new RequirementRevision(high.Id, 1, "High-level behavior.", "Acceptance test", "Test",
                RequirementRevisionState.Active, change.Id, baseline.Id, now,
                RequirementParentKind.Allocated, parentRevisionIds: [systemRevision.Id]);
            var revision = new RequirementRevision(artifact.Id, 1, "No implementation change is required.",
                "Acceptance test", "Test", RequirementRevisionState.Active, change.Id, baseline.Id, now,
                RequirementParentKind.Allocated, parentRevisionIds: [highRevision.Id]);
            var campaign = new ReleaseCampaign(data.ProjectId, data.ReleaseId, baseline.Id,
                "Acceptance campaign", data.UserName, now);
            artifactId = artifact.Id;
            revisionId = revision.Id;
            baselineId = baseline.Id;
            db.AddRange(baseline, system, systemRevision, high, highRevision, artifact, change, revision, campaign,
                new BaselineRequirementSelection(baseline.Id, system.Id, systemRevision.Id),
                new BaselineRequirementSelection(baseline.Id, high.Id, highRevision.Id),
                new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id),
                new RequirementTraceLink(data.ProjectId, highRevision.Id, systemRevision.Id,
                    RequirementTraceType.AllocatedFrom, "Exact parent", now),
                new RequirementTraceLink(data.ProjectId, revision.Id, highRevision.Id,
                    RequirementTraceType.AllocatedFrom, "Exact parent", now));
            await db.SaveChangesAsync();
            await db.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.State, CandidateBaselineState.Frozen)
                .SetProperty(x => x.RequirementsMaterializedAt, now));
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var response = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", new
        {
            releaseId = data.ReleaseId, requirementArtifactId = artifactId, requirementRevisionId = revisionId,
            expectedBaselineId = baselineId, disposition = "NoCodeChangeRequired", expectedSelectorVersion = 0L, expectedLegacyRecordId = (Guid?)null,
            contributions = Array.Empty<object>(), noCodeChangeRationale = "The exact changed requirement has no implementation code impact."
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, transport.Calls);
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Single(await saved.CodeEvidenceDispositionSets.Where(x => x.ProjectId == data.ProjectId).ToListAsync());
        Assert.Empty(await saved.CodeEvidenceContributions.Where(x => x.ProjectId == data.ProjectId).ToListAsync());
    }

    [Fact]
    public async Task Evidence_endpoint_accepts_source_less_merge_and_prior_event_file_after_fresh_provider_checks()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string squashSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string otherSha = "cccccccccccccccccccccccccccccccccccccccc";
        const string nonAncestorSha = "dddddddddddddddddddddddddddddddddddddddd";
        var providerMode = "merge";
        using var transport = new Remote(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/approvals", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"approved_by\":[]}") };
            if (path.Contains("/merge_requests/12", StringComparison.Ordinal))
            {
                var mergeSha = providerMode == "squash" ? null : providerMode == "unknown" ? null : providerMode == "nonancestor" ? otherSha : sha;
                var squash = providerMode == "squash" ? squashSha : null;
                return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"id\":1200,\"project_id\":17,\"iid\":12,\"title\":\"Merged change\",\"state\":\"merged\",\"draft\":false,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/12\",\"sha\":\"{sha}\",\"merge_commit_sha\":{(mergeSha is null ? "null" : $"\"{mergeSha}\"")},\"squash_merge_commit_sha\":{(squash is null ? "null" : $"\"{squash}\"")},\"merged_at\":\"2026-09-19T12:00:00Z\"}}") };
            }
            if (path.EndsWith("/repository/merge_base", StringComparison.Ordinal))
            {
                var mergeBase = providerMode == "nonancestor" ? nonAncestorSha : providerMode == "squash" ? squashSha : sha;
                return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"id\":\"{mergeBase}\"}}") };
            }
            if (path.EndsWith("/repository/tree", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent($"[{{\"id\":\"{sha}\",\"name\":\"demo.c\",\"path\":\"src/demo.c\",\"type\":\"blob\",\"mode\":\"100644\"}}]") };
            return new(HttpStatusCode.ServiceUnavailable);
        });
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services);
        Guid baselineId;
        Guid artifactId;
        Guid revisionId;
        Guid snapshotId;
        Guid currentEventId;
        Guid mergeId;
        Guid fileId;
        long configurationVersion;
        var now = DateTimeOffset.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var baseline = new CandidateBaseline("BL-900002", 0, data.ProjectId, data.ReleaseId, null,
                "Acceptance baseline", data.UserName, now);
            var system = new RequirementArtifact(data.ProjectId, "SYS-900002", RequirementLevel.System, now);
            var high = new RequirementArtifact(data.ProjectId, "HLR-900002", RequirementLevel.HighLevel, now);
            var artifact = new RequirementArtifact(data.ProjectId, "LLR-900002", RequirementLevel.LowLevel, now);
            var change = new SystemChangeRequest("LLRCR-90002", 0, data.ProjectId, data.ReleaseId,
                "GitLab acceptance", "Problem", "Analysis", "Solution", data.UserName, now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel);
            var systemRevision = new RequirementRevision(system.Id, 1, "System behavior.", "Acceptance test", "Test",
                RequirementRevisionState.Active, change.Id, baseline.Id, now);
            var highRevision = new RequirementRevision(high.Id, 1, "High-level behavior.", "Acceptance test", "Test",
                RequirementRevisionState.Active, change.Id, baseline.Id, now,
                RequirementParentKind.Allocated, parentRevisionIds: [systemRevision.Id]);
            var revision = new RequirementRevision(artifact.Id, 1, "Implementation behavior.", "Acceptance test", "Test",
                RequirementRevisionState.Active, change.Id, baseline.Id, now,
                RequirementParentKind.Allocated, parentRevisionIds: [highRevision.Id]);
            var campaign = new ReleaseCampaign(data.ProjectId, data.ReleaseId, baseline.Id,
                "Acceptance campaign", data.UserName, now);
            var repository = await db.ProjectRepositoryConfigurations.SingleAsync(x => x.ProjectId == data.ProjectId);
            var snapshot = new GitLabSourceSnapshot(data.ProjectId, repository.Id, "https://gitlab.example", 17,
                "group/project", sha, "main", data.UserName, now, repository.Version);
            var firstEvent = new GitLabSourceSelectionEvent(data.ProjectId, data.ReleaseId, snapshot.Id, 0, data.UserName, now);
            var currentEvent = new GitLabSourceSelectionEvent(data.ProjectId, data.ReleaseId, snapshot.Id, 1, data.UserName, now);
            var current = new GitLabCurrentSourceSelection(data.ProjectId, data.ReleaseId, snapshot.Id, firstEvent.Id, data.UserName, now);
            current.Move(1, snapshot.Id, currentEvent.Id, data.UserName, now);
            var target = CodeRelationshipTarget.ForRequirementRevision(revision.Id, artifact.Id, revision.Revision, "LLR-900002.01");
            var merge = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, snapshot.InstanceBaseUrl, 17,
                12, 1200, null, null, snapshot.PathWithNamespace,
                "https://gitlab.example/group/project/-/merge_requests/12", "Recorded context", target,
                CodeRelationshipMeaning.Implements, data.UserName, now);
            var file = new GitLabFileRelationship(data.ProjectId, data.ReleaseId, snapshot.InstanceBaseUrl, 17,
                snapshot.Id, firstEvent.Id, sha, "src/demo.c", 1, 3, 12, target,
                CodeRelationshipMeaning.Implements, data.UserName, now);
            baselineId = baseline.Id; artifactId = artifact.Id; revisionId = revision.Id; snapshotId = snapshot.Id;
            currentEventId = currentEvent.Id; mergeId = merge.Id; fileId = file.Id; configurationVersion = repository.Version;
            db.AddRange(baseline, system, systemRevision, high, highRevision, artifact, change, revision, campaign,
                new BaselineRequirementSelection(baseline.Id, system.Id, systemRevision.Id),
                new BaselineRequirementSelection(baseline.Id, high.Id, highRevision.Id),
                new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id),
                new RequirementTraceLink(data.ProjectId, highRevision.Id, systemRevision.Id,
                    RequirementTraceType.AllocatedFrom, "Exact parent", now),
                new RequirementTraceLink(data.ProjectId, revision.Id, highRevision.Id,
                    RequirementTraceType.AllocatedFrom, "Exact parent", now),
                snapshot, firstEvent, currentEvent, current, merge, file);
            await db.SaveChangesAsync();
            await db.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.State, CandidateBaselineState.Frozen)
                .SetProperty(x => x.RequirementsMaterializedAt, now));
        }

        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        object Payload(long selectorVersion) => new
        {
            releaseId = data.ReleaseId, expectedBaselineId = baselineId, requirementArtifactId = artifactId,
            requirementRevisionId = revisionId, disposition = "GitLabContributions", expectedSelectorVersion = selectorVersion,
            expectedLegacyRecordId = (Guid?)null, expectedConfigurationVersion = configurationVersion,
            expectedSourceSelectionEventId = currentEventId, expectedSourceSnapshotId = snapshotId,
            expectedSourceSelectionVersion = 2L,
            contributions = new[]
            {
                new { kind = "MergeRequest", relationshipId = mergeId, expectedRelationshipVersion = 1L,
                    parentPath = (string?)null, cursor = (string?)null, pageSize = (int?)null },
                new { kind = "File", relationshipId = fileId, expectedRelationshipVersion = 1L,
                    parentPath = (string?)"src", cursor = (string?)null, pageSize = (int?)20 }
            },
            noCodeChangeRationale = (string?)null
        };
        using var response = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", Payload(0));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        providerMode = "squash";
        using var squash = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", Payload(1));
        Assert.Equal(HttpStatusCode.Created, squash.StatusCode);
        providerMode = "unknown";
        using var unknown = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", Payload(2));
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        providerMode = "nonancestor";
        using var nonAncestor = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", Payload(2));
        Assert.Equal(HttpStatusCode.Conflict, nonAncestor.StatusCode);
        Assert.Equal(13, transport.Calls);
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var contributions = await saved.CodeEvidenceContributions.Where(x => x.ProjectId == data.ProjectId).ToListAsync();
        Assert.Equal(4, contributions.Count);
        Assert.Equal(2, await saved.CodeEvidenceDispositionSets.CountAsync(x => x.ProjectId == data.ProjectId));
        var latestEvidenceId = await saved.CodeEvidenceCurrentSelectors.Where(x => x.ProjectId == data.ProjectId)
            .Select(x => x.EvidenceSetId).SingleAsync();
        var latestMerge = contributions.Single(x => x.EvidenceSetId == latestEvidenceId
            && x.ContributionKind == CodeEvidenceContributionKind.MergeRequest);
        Assert.Equal(squashSha, latestMerge.MergeResultSha);
        Assert.Equal(GitLabMergeResultKind.SquashCommit, latestMerge.MergeResultKind);
        Assert.Equal(2, contributions.Count(x => x.ContributionKind == CodeEvidenceContributionKind.File));
        Assert.All(contributions.Where(x => x.ContributionKind == CodeEvidenceContributionKind.File),
            x => Assert.Equal("src/demo.c", x.FilePath));
    }

    /// <summary>
    /// The read projection's capabilities are what the UI offers. Each row needs a discriminating control: a
    /// mutating engineer on an in-work build is offered exactly the action the row's state allows, while a
    /// view-only role or a released build is offered neither, whatever the row's state.
    /// </summary>
    [Theory]
    [InlineData(ProgramRole.Engineer, false, false, true, false)]
    [InlineData(ProgramRole.Engineer, false, true, false, true)]
    [InlineData(ProgramRole.Reviewer, false, false, false, false)]
    [InlineData(ProgramRole.Reviewer, false, true, false, false)]
    [InlineData(ProgramRole.Engineer, true, false, false, false)]
    [InlineData(ProgramRole.Engineer, true, true, false, false)]
    public async Task Relationship_read_offers_only_the_mutation_the_role_build_and_row_state_allow(
        ProgramRole role, bool releaseIsReleased, bool withdrawn, bool canWithdraw, bool canReAdd)
    {
        using var factory = Configure(new AeroLinkApiFactory(), new Remote(_ => new(HttpStatusCode.ServiceUnavailable)));
        var data = await SeedAsync(factory.Services, releaseIsReleased: releaseIsReleased, role: role);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var target = CodeRelationshipTarget.ForChangeRequestRevision(Guid.NewGuid(), 1, "SCR-1.01");
            var row = new GitLabMergeRequestRelationship(data.ProjectId, data.ReleaseId, "https://gitlab.example", 17, 22,
                2200, null, null, "group/project", "https://gitlab.example/group/project/-/merge_requests/22",
                "Recorded context", target, CodeRelationshipMeaning.RelatedContext, data.UserName, DateTimeOffset.UtcNow);
            if (withdrawn) row.Withdraw(row.Version, data.UserName, "Recorded against the wrong change.", DateTimeOffset.UtcNow);
            db.Add(row);
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);
        using var response = await client.GetAsync($"/api/projects/{data.ProjectId}/code/relationships?releaseId={data.ReleaseId}&includeWithdrawn=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(!withdrawn, item.GetProperty("isActive").GetBoolean());
        var capabilities = item.GetProperty("capabilities");
        Assert.Equal(canWithdraw, capabilities.GetProperty("canWithdraw").GetBoolean());
        Assert.Equal(canReAdd, capabilities.GetProperty("canReAdd").GetBoolean());
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
