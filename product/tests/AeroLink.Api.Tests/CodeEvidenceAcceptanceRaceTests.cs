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

public sealed class CodeEvidenceAcceptanceRaceTests
{
    [Theory]
    [InlineData("source", HttpStatusCode.Conflict)]
    [InlineData("configuration", HttpStatusCode.Conflict)]
    [InlineData("review", HttpStatusCode.Conflict)]
    [InlineData("reopen", HttpStatusCode.Conflict)]
    [InlineData("authority", HttpStatusCode.Forbidden)]
    public async Task Deferred_provider_preflight_rechecks_source_review_and_authority(string change,
        HttpStatusCode expectedStatus)
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var transport = new DeferredRemote(_ => TreeResponse(sha, "src/demo.c", "blob", "100644"));
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services);
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);

        var request = client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", FilePayload(data, 0));
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await ApplyChangeAsync(factory.Services, data, change);
        }
        finally
        {
            transport.Release();
        }

        using var response = await request;
        Assert.Equal(expectedStatus, response.StatusCode);
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db.CodeEvidenceDispositionSets.Where(x => x.ProjectId == data.ProjectId).ToListAsync());
        Assert.Empty(await db.CodeEvidenceContributions.Where(x => x.ProjectId == data.ProjectId).ToListAsync());
    }

    [Fact]
    public async Task Mixed_merge_and_file_acceptance_commits_atomically_and_replaces_only_with_fresh_selector_version()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var transport = new Remote(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/merge_base", StringComparison.Ordinal))
                return JsonResponse($"{{\"id\":\"{sha}\"}}");
            if (path.EndsWith("/repository/tree", StringComparison.Ordinal))
                return TreeResponse(sha, "src/demo.c", "blob", "100644");
            if (path.Contains("/merge_requests/12", StringComparison.Ordinal))
                return JsonResponse($"{{\"id\":1200,\"project_id\":17,\"iid\":12,\"title\":\"Merged change\",\"state\":\"merged\",\"draft\":false,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/12\",\"sha\":\"{sha}\",\"merge_commit_sha\":\"{sha}\",\"squash_merge_commit_sha\":null,\"merged_at\":\"2026-09-19T12:00:00Z\"}}");
            return new(HttpStatusCode.ServiceUnavailable);
        });
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services);
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);

        using var first = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", MixedPayload(data, 0));
        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        await AssertEvidenceCountsAsync(factory.Services, data.ProjectId, expectedSets: 1, expectedContributions: 2,
            expectedSelectorVersion: 1);

        using var stale = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", MixedPayload(data, 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await AssertEvidenceCountsAsync(factory.Services, data.ProjectId, expectedSets: 1, expectedContributions: 2,
            expectedSelectorVersion: 1);

        using var replacement = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", MixedPayload(data, 1));
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        await AssertEvidenceCountsAsync(factory.Services, data.ProjectId, expectedSets: 2, expectedContributions: 4,
            expectedSelectorVersion: 2);
    }

    [Fact]
    public async Task Non_implements_merge_context_rolls_back_after_successful_provider_preflight()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var transport = new Remote(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/merge_base", StringComparison.Ordinal))
                return JsonResponse($"{{\"id\":\"{sha}\"}}");
            if (path.Contains("/merge_requests/12", StringComparison.Ordinal))
                return JsonResponse($"{{\"id\":1200,\"project_id\":17,\"iid\":12,\"title\":\"Context only\",\"state\":\"merged\",\"draft\":false,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/12\",\"sha\":\"{sha}\",\"merge_commit_sha\":\"{sha}\",\"squash_merge_commit_sha\":null,\"merged_at\":\"2026-09-19T12:00:00Z\"}}");
            return new(HttpStatusCode.ServiceUnavailable);
        });
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services, mergeMeaning: CodeRelationshipMeaning.RelatedContext);
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);

        using var response = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", MergePayload(data, 0));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertEvidenceCountsAsync(factory.Services, data.ProjectId, expectedSets: 0, expectedContributions: 0,
            expectedSelectorVersion: null);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("symlink")]
    public async Task Missing_or_non_blob_file_provider_result_rolls_back_without_evidence(string treeKind)
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var transport = new Remote(request => request.RequestUri!.AbsolutePath.EndsWith("/repository/tree", StringComparison.Ordinal)
            ? treeKind == "missing" ? new(HttpStatusCode.OK) { Content = new StringContent("[]") }
            : TreeResponse(sha, "src/demo.c", "blob", "120000")
            : new(HttpStatusCode.ServiceUnavailable));
        using var factory = Configure(new AeroLinkApiFactory(), transport);
        var data = await SeedAsync(factory.Services);
        using var client = factory.CreateClient();
        await SignInAsync(client, data.UserName);

        using var response = await client.PostAsJsonAsync($"/api/projects/{data.ProjectId}/code/evidence", FilePayload(data, 0));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertEvidenceCountsAsync(factory.Services, data.ProjectId, expectedSets: 0, expectedContributions: 0,
            expectedSelectorVersion: null);
    }

    private static WebApplicationFactory<Program> Configure(AeroLinkApiFactory factory, HttpMessageHandler transport) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<ProjectGitLabOptions>(options =>
            {
                options.BaseUrl = "https://gitlab.example";
                options.ReadAccessToken = "test-only-token";
            });
            services.AddHttpClient<GitLabMetadataReader>().ConfigurePrimaryHttpMessageHandler(() => transport);
        }));

    private static async Task<AcceptanceData> SeedAsync(IServiceProvider services,
        CodeRelationshipMeaning mergeMeaning = CodeRelationshipMeaning.Implements)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var program = new ProgramRecord("Evidence race " + tag, "ER" + tag);
        var project = new ProjectRecord(program.Id, "Evidence race project", "Software");
        var predecessor = new SoftwareRelease(project.Id, "0.9", true);
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var user = new UserAccount("evidence.race." + tag, "Evidence race engineer", tag + "@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var repository = new ProjectRepositoryConfiguration(project.Id, ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/group/project", "test.setup", now);
        repository.RecordVerification("test.setup", now, 17, "group/project");
        var sourceBaseline = new CandidateBaseline("BL-900004", 0, project.Id, predecessor.Id, null,
            "Prior", user.UserName, now);
        var baseline = new CandidateBaseline("BL-900003", 0, project.Id, release.Id, sourceBaseline.Id,
            "Acceptance", user.UserName, now);
        var system = new RequirementArtifact(project.Id, "SYS-900003", RequirementLevel.System, now);
        var high = new RequirementArtifact(project.Id, "HLR-900003", RequirementLevel.HighLevel, now);
        var artifact = new RequirementArtifact(project.Id, "LLR-900003", RequirementLevel.LowLevel, now);
        var change = new SystemChangeRequest("LLRCR-90003", 0, project.Id, release.Id,
            "Evidence race requirement", "Problem", "Analysis", "Solution", user.UserName, now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel);
        var systemRevision = new RequirementRevision(system.Id, 1, "System behavior.", "Test", "Test",
            RequirementRevisionState.Active, change.Id, baseline.Id, now);
        var highRevision = new RequirementRevision(high.Id, 1, "High-level behavior.", "Test", "Test",
            RequirementRevisionState.Active, change.Id, baseline.Id, now, RequirementParentKind.Allocated,
            parentRevisionIds: [systemRevision.Id]);
        var revision = new RequirementRevision(artifact.Id, 1, "Implementation behavior.", "Test", "Test",
            RequirementRevisionState.Active, change.Id, baseline.Id, now, RequirementParentKind.Allocated,
            parentRevisionIds: [highRevision.Id]);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Evidence race campaign", user.UserName, now);
        var snapshot = new GitLabSourceSnapshot(project.Id, repository.Id, "https://gitlab.example", 17,
            "group/project", sha, "main", user.UserName, now, repository.Version);
        var selection = new GitLabSourceSelectionEvent(project.Id, release.Id, snapshot.Id, 0, user.UserName, now);
        var current = new GitLabCurrentSourceSelection(project.Id, release.Id, snapshot.Id, selection.Id, user.UserName, now);
        var target = CodeRelationshipTarget.ForRequirementRevision(revision.Id, artifact.Id, revision.Revision,
            artifact.BaseNumber + ".01");
        var merge = new GitLabMergeRequestRelationship(project.Id, release.Id, snapshot.InstanceBaseUrl, 17, 12, 1200,
            null, null, snapshot.PathWithNamespace,
            "https://gitlab.example/group/project/-/merge_requests/12", "Recorded MR", target, mergeMeaning,
            user.UserName, now);
        var file = new GitLabFileRelationship(project.Id, release.Id, snapshot.InstanceBaseUrl, 17, snapshot.Id,
            selection.Id, sha, "src/demo.c", 1, 3, 12, target, CodeRelationshipMeaning.Implements, user.UserName, now);
        db.AddRange(program, project, predecessor, release, user, repository, sourceBaseline, baseline, system, systemRevision, high, highRevision,
            artifact, change, revision, campaign,
            new ProgramMembership(user.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new BaselineRequirementSelection(baseline.Id, system.Id, systemRevision.Id),
            new BaselineRequirementSelection(baseline.Id, high.Id, highRevision.Id),
            new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id),
            new RequirementTraceLink(project.Id, highRevision.Id, systemRevision.Id,
                RequirementTraceType.AllocatedFrom, "Exact parent", now),
            new RequirementTraceLink(project.Id, revision.Id, highRevision.Id,
                RequirementTraceType.AllocatedFrom, "Exact parent", now),
            snapshot, selection, current, merge, file);
        await db.SaveChangesAsync();
        await db.CandidateBaselines.Where(x => x.Id == baseline.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, CandidateBaselineState.Frozen)
            .SetProperty(x => x.RequirementsMaterializedAt, now));
        return new(project.Id, release.Id, user.UserName, user.Id, baseline.Id, artifact.Id, revision.Id, repository.Id,
            repository.Version, snapshot.Id, selection.Id, merge.Id, file.Id, sha, project.ProgramId);
    }

    private static async Task ApplyChangeAsync(IServiceProvider services, AcceptanceData data, string change)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        switch (change)
        {
            case "source":
            {
                var repository = await db.ProjectRepositoryConfigurations.SingleAsync(x => x.Id == data.RepositoryId);
                var snapshot = new GitLabSourceSnapshot(data.ProjectId, repository.Id, "https://gitlab.example", 17,
                    "group/project", new string('b', 40), "main", data.UserName, now, repository.Version);
                var eventRow = new GitLabSourceSelectionEvent(data.ProjectId, data.ReleaseId, snapshot.Id, 1,
                    data.UserName, now);
                var current = await db.GitLabCurrentSourceSelections.SingleAsync(x => x.ProjectId == data.ProjectId
                    && x.ReleaseId == data.ReleaseId);
                current.Move(1, snapshot.Id, eventRow.Id, data.UserName, now);
                db.AddRange(snapshot, eventRow);
                break;
            }
            case "configuration":
            {
                var repository = await db.ProjectRepositoryConfigurations.SingleAsync(x => x.Id == data.RepositoryId);
                repository.Configure(repository.Version, ProjectRepositorySetupMode.ConnectNow, "GitLab",
                    "https://gitlab.example/group/project", data.UserName, now);
                break;
            }
            case "review":
            {
                await db.ReleaseCampaigns.Where(x => x.ProjectId == data.ProjectId && x.ReleaseId == data.ReleaseId)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.State, ReleaseCampaignState.InReview)
                        .SetProperty(x => x.Version, x => x.Version + 1));
                return;
            }
            case "reopen":
            {
                var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == data.BaselineId);
                baseline.Reopen(data.UserName, "Reopen during deferred provider observation.", now);
                break;
            }
            case "authority":
            {
                var membership = await db.ProgramMemberships.SingleAsync(x => x.ProgramId == data.ProgramId
                    && x.UserId == data.UserId);
                membership.End(data.UserName, now);
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown race mutation.");
        }
        await db.SaveChangesAsync();
    }

    private static async Task AssertEvidenceCountsAsync(IServiceProvider services, Guid projectId,
        int expectedSets, int expectedContributions, long? expectedSelectorVersion)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(expectedSets, await db.CodeEvidenceDispositionSets.CountAsync(x => x.ProjectId == projectId));
        Assert.Equal(expectedContributions, await db.CodeEvidenceContributions.CountAsync(x => x.ProjectId == projectId));
        var selector = await db.CodeEvidenceCurrentSelectors.SingleOrDefaultAsync(x => x.ProjectId == projectId);
        if (expectedSelectorVersion is null) Assert.Null(selector);
        else Assert.Equal(expectedSelectorVersion, selector?.Version);
    }

    private static object FilePayload(AcceptanceData data, long selectorVersion) => new
    {
        releaseId = data.ReleaseId, expectedBaselineId = data.BaselineId,
        requirementArtifactId = data.ArtifactId, requirementRevisionId = data.RevisionId,
        disposition = "GitLabContributions", expectedSelectorVersion = selectorVersion,
        expectedLegacyRecordId = (Guid?)null, expectedConfigurationVersion = data.ConfigurationVersion,
        expectedSourceSelectionEventId = data.SelectionId, expectedSourceSnapshotId = data.SnapshotId,
        expectedSourceSelectionVersion = 1L,
        contributions = new[] { new
        {
            kind = "File", relationshipId = data.FileId, expectedRelationshipVersion = 1L,
            parentPath = "src", cursor = (string?)null, pageSize = (int?)20
        }},
        noCodeChangeRationale = (string?)null
    };

    private static object MergePayload(AcceptanceData data, long selectorVersion) => new
    {
        releaseId = data.ReleaseId, expectedBaselineId = data.BaselineId,
        requirementArtifactId = data.ArtifactId, requirementRevisionId = data.RevisionId,
        disposition = "GitLabContributions", expectedSelectorVersion = selectorVersion,
        expectedLegacyRecordId = (Guid?)null, expectedConfigurationVersion = data.ConfigurationVersion,
        expectedSourceSelectionEventId = data.SelectionId, expectedSourceSnapshotId = data.SnapshotId,
        expectedSourceSelectionVersion = 1L,
        contributions = new[] { new
        {
            kind = "MergeRequest", relationshipId = data.MergeId, expectedRelationshipVersion = 1L,
            parentPath = (string?)null, cursor = (string?)null, pageSize = (int?)null
        }},
        noCodeChangeRationale = (string?)null
    };

    private static object MixedPayload(AcceptanceData data, long selectorVersion) => new
    {
        releaseId = data.ReleaseId, expectedBaselineId = data.BaselineId,
        requirementArtifactId = data.ArtifactId, requirementRevisionId = data.RevisionId,
        disposition = "GitLabContributions", expectedSelectorVersion = selectorVersion,
        expectedLegacyRecordId = (Guid?)null, expectedConfigurationVersion = data.ConfigurationVersion,
        expectedSourceSelectionEventId = data.SelectionId, expectedSourceSnapshotId = data.SnapshotId,
        expectedSourceSelectionVersion = 1L,
        contributions = new object[]
        {
            new { kind = "MergeRequest", relationshipId = data.MergeId, expectedRelationshipVersion = 1L,
                parentPath = (string?)null, cursor = (string?)null, pageSize = (int?)null },
            new { kind = "File", relationshipId = data.FileId, expectedRelationshipVersion = 1L,
                parentPath = (string?)"src", cursor = (string?)null, pageSize = (int?)20 }
        },
        noCodeChangeRationale = (string?)null
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage TreeResponse(string sha, string path, string type, string mode) =>
        JsonResponse($"[{{\"id\":\"{sha}\",\"name\":\"demo.c\",\"path\":\"{path}\",\"type\":\"{type}\",\"mode\":\"{mode}\"}}]");

    private static async Task SignInAsync(HttpClient client, string userName) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword })).StatusCode);

    private sealed record AcceptanceData(Guid ProjectId, Guid ReleaseId, string UserName, Guid UserId,
        Guid BaselineId, Guid ArtifactId, Guid RevisionId, Guid RepositoryId, long ConfigurationVersion,
        Guid SnapshotId, Guid SelectionId, Guid MergeId, Guid FileId, string CommitSha, Guid ProgramId);

    private sealed class Remote(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class DeferredRemote(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => release.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return respond(request);
        }
    }
}
