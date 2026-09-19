using System.Net;
using System.Text.Json;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace AeroLink.Infrastructure.Tests;

public sealed class GitLabRegisterObservationTests
{
    private static readonly string Ancestor = new('a', 40);
    private static readonly string Descendant = new('b', 40);

    [Fact]
    public async Task Register_page_observes_only_requested_identities_with_one_request_and_no_invented_missing_rows()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("/api/v4/projects/17/merge_requests", request.RequestUri!.AbsolutePath);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            Assert.Contains("iids[]=3", query); Assert.Contains("iids[]=9", query);
            Assert.Contains("per_page=2", query); Assert.Contains("state=all", query);
            return Json("[" + Row(3) + "]");
        });
        var result = await Reader(handler).ReadMergeRequestSummariesAsync(Configuration(), [9, 3, 3], default);
        Assert.True(result.Succeeded);
        Assert.Equal(3, Assert.Single(result.Value!).Iid);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrequested_or_duplicate_MRs_in_a_batch_are_invalid_observations(bool duplicate)
    {
        var body = duplicate ? "[" + Row(3) + "," + Row(3) + "]" : "[" + Row(4) + "]";
        var result = await Reader(new Handler(_ => Json(body)))
            .ReadMergeRequestSummariesAsync(Configuration(), [3], default);
        Assert.Equal(GitLabMetadataStatus.InvalidResponse, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Batch_limits_are_enforced_before_network_access()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("No request permitted"));
        var reader = Reader(handler);
        foreach (var ids in new[] { Array.Empty<int>(), new[] { -1 }, Enumerable.Range(1, 101).ToArray() })
            Assert.Equal(GitLabMetadataStatus.InvalidRequest,
                (await reader.ReadMergeRequestSummariesAsync(Configuration(), ids, default)).Status);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exact_ancestry_is_distinct_from_a_merge_outside_the_selected_source(bool incorporated)
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("/api/v4/projects/17/repository/merge_base", request.RequestUri!.AbsolutePath);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            Assert.Contains("refs[]=" + Ancestor, query);
            Assert.Contains("refs[]=" + Descendant, query);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Json(JsonSerializer.Serialize(new { id = incorporated ? Ancestor : new string('c', 40),
                author_email = "not-imported@example.test", message = "Not imported provider message" }));
        });
        var result = await Reader(handler).ReadCommitAncestryAsync(Configuration(), Ancestor.ToUpperInvariant(), Descendant, default);
        Assert.True(result.Succeeded);
        Assert.Equal(incorporated, result.Value!.IsAncestor);
        Assert.Equal(Ancestor, result.Value.AncestorSha);
        Assert.Equal(Descendant, result.Value.DescendantSha);
        Assert.DoesNotContain("not-imported", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("provider message", JsonSerializer.Serialize(result));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, GitLabMetadataStatus.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, GitLabMetadataStatus.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, GitLabMetadataStatus.RateLimited)]
    public async Task Failed_ancestry_observation_is_unknown_rather_than_a_negative_or_positive_answer(
        HttpStatusCode status, GitLabMetadataStatus expected)
    {
        var result = await Reader(new Handler(_ => new HttpResponseMessage(status)))
            .ReadCommitAncestryAsync(Configuration(), Ancestor, Descendant, default);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(41)]
    [InlineData(63)]
    public async Task Neither_requests_nor_responses_can_claim_partial_commit_identities(int length)
    {
        var handler = new Handler(_ => Json("{\"id\":\"" + new string('c', length) + "\"}"));
        var reader = Reader(handler);
        Assert.Equal(GitLabMetadataStatus.InvalidRequest,
            (await reader.ReadCommitAncestryAsync(Configuration(), new string('a', length), Descendant, default)).Status);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(GitLabMetadataStatus.InvalidResponse,
            (await reader.ReadCommitAncestryAsync(Configuration(), Ancestor, Descendant, default)).Status);
        Assert.Equal(1, handler.Calls);
    }

    private static string Row(int iid) => JsonSerializer.Serialize(new {
        id = 100 + iid, project_id = 17, iid, title = "Synthetic MR", state = "opened", draft = false,
        web_url = $"https://gitlab.example/group/project/-/merge_requests/{iid}"
    });
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static GitLabMetadataReader Reader(Handler handler) => new(new HttpClient(handler),
        Options.Create(new ProjectGitLabOptions { BaseUrl = "https://gitlab.example", ReadAccessToken = "test-only-token" }));
    private static ProjectRepositoryConfiguration Configuration()
    {
        var config = new ProjectRepositoryConfiguration(Guid.NewGuid(), ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/group/project", "tester", DateTimeOffset.UtcNow);
        config.RecordVerification("tester", DateTimeOffset.UtcNow, 17, "group/project");
        return config;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response(request)); }
    }
}
