using System.Net;
using System.Net.Http.Headers;
using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AeroLink.Infrastructure.Tests;

public sealed class GitLabMetadataReaderTests
{
    private static readonly string Sha = new('a', 40);

    [Fact]
    public void ProductionRegistrationKeepsMetadataReaderGetOnlyAndCredentialBoundaryBounded()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAeroLinkInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(GitLabMetadataReader));
        while (handler is DelegatingHandler wrapper) handler = wrapper.InnerHandler!;
        var transport = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseCookies);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GitLabMetadataReader));
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
    }

    [Fact]
    public async Task UnverifiedOrMismatchedConfigurationMakesNoCredentialBearingRequest()
    {
        var handler = new Handler(_ => throw new Exception("No request permitted"));
        var reader = Reader(handler);
        var unverified = new ProjectRepositoryConfiguration(Guid.NewGuid(), ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/group/project", "tester", DateTimeOffset.UtcNow);
        var pending = await reader.DiscoverMergeRequestsAsync(unverified, new(), default);
        Assert.Equal(GitLabMetadataStatus.RepositoryUnverified, pending.Status);
        Assert.Equal(0, handler.Calls);

        var wrongEndpoint = new ProjectRepositoryConfiguration(Guid.NewGuid(), ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://other.example/group/project", "tester", DateTimeOffset.UtcNow);
        wrongEndpoint.RecordVerification("tester", DateTimeOffset.UtcNow, 86663796, "group/project");
        var mismatch = await reader.DiscoverMergeRequestsAsync(wrongEndpoint, new(), default);
        Assert.Equal("identity_mismatch", mismatch.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DiscoveryReturnsMinimalRowsAndTruthfulNextPageState()
    {
        HttpRequestMessage? seen = null;
        var handler = new Handler(request =>
        {
            seen = request;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("test-only-token", Assert.Single(request.Headers.GetValues("PRIVATE-TOKEN")));
            return Json("[{\"id\":1001,\"project_id\":86663796,\"iid\":7,\"title\":\"<text>\",\"state\":\"opened\",\"draft\":true,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/7\",\"author\":{\"email\":\"secret@example.test\"}}]", ("X-Next-Page", "2"));
        });
        var result = await Reader(handler).DiscoverMergeRequestsAsync(Configuration(), new(Search: "nav", State: "opened", Page: 1, PerPage: 20), default);
        Assert.True(result.Succeeded);
        Assert.Equal(GitLabMetadataCompleteness.Partial, result.Completeness);
        Assert.Equal("2", result.NextPage);
        var row = Assert.Single(result.Value!);
        Assert.Equal(1001, row.Id);
        Assert.Equal(7, row.Iid);
        Assert.Equal("<text>", row.Title);
        Assert.True(row.Draft);
        Assert.DoesNotContain("email", string.Join("|", row.GetType().GetProperties().Select(x => x.Name)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("projects/86663796/merge_requests", seen!.RequestUri!.AbsoluteUri);
        Assert.Contains("search=nav", seen.RequestUri.Query);
    }

    [Fact]
    public async Task DetailReadsActualApprovedByIdentitiesWithoutTreatingRulesAsApproval()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/approvals", StringComparison.Ordinal)
            ? Json("{\"approved\":true,\"approved_by\":[{\"user\":{\"id\":41,\"username\":\"reviewer\",\"name\":\"Review Person\",\"email\":\"secret@example.test\"}}]}")
            : Json($"{{\"id\":1001,\"project_id\":86663796,\"iid\":7,\"title\":\"MR\",\"state\":\"opened\",\"draft\":false,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/7\",\"sha\":\"{Sha}\"}}"));
        var result = await Reader(handler).GetMergeRequestAsync(Configuration(), 7, default);
        Assert.True(result.Succeeded);
        var approval = result.Value!.Approvals;
        Assert.True(approval.Known);
        var user = Assert.Single(approval.ApprovedBy);
        Assert.Equal(41, user.Id);
        Assert.Equal("reviewer", user.Username);
        Assert.Equal("Review Person", user.Name);
        Assert.DoesNotContain("secret", user.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task EmptyApprovedByIsKnownZeroWhileUnavailableApprovalsRemainUnknown()
    {
        var empty = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/approvals", StringComparison.Ordinal)
            ? Json("{\"approved\":true,\"approved_by\":[]}")
            : Json($"{{\"id\":1001,\"project_id\":86663796,\"iid\":7,\"title\":\"MR\",\"state\":\"opened\",\"draft\":false,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/7\"}}"));
        var known = await Reader(empty).GetMergeRequestAsync(Configuration(), 7, default);
        Assert.True(known.Value!.Approvals.Known);
        Assert.Empty(known.Value.Approvals.ApprovedBy);

        var unavailable = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/approvals", StringComparison.Ordinal)
            ? new(HttpStatusCode.Forbidden)
            : Json($"{{\"id\":1001,\"project_id\":86663796,\"iid\":7,\"title\":\"MR\",\"state\":\"opened\",\"draft\":false,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/7\"}}"));
        var unknown = await Reader(unavailable).GetMergeRequestAsync(Configuration(), 7, default);
        Assert.False(unknown.Value!.Approvals.Known);
        Assert.Equal("forbidden", unknown.Value.Approvals.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GitLabMetadataStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, GitLabMetadataStatus.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, GitLabMetadataStatus.NotFound)]
    [InlineData((HttpStatusCode)429, GitLabMetadataStatus.RateLimited)]
    [InlineData(HttpStatusCode.Redirect, GitLabMetadataStatus.RedirectRejected)]
    public async Task RemoteFailuresStayDistinctAndNeverBecomeSuccessfulMetadata(HttpStatusCode status, GitLabMetadataStatus expected)
    {
        var result = await Reader(new Handler(_ => new(status))).DiscoverMergeRequestsAsync(Configuration(), new(), default);
        Assert.Equal(expected, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task CanceledRemoteWaitIsReportedAsTimeoutWhenCallerDidNotCancel()
    {
        var result = await Reader(new Handler(_ => throw new OperationCanceledException()))
            .DiscoverMergeRequestsAsync(Configuration(), new(), default);
        Assert.Equal(GitLabMetadataStatus.Timeout, result.Status);
        Assert.Equal("timeout", result.Code);
    }

    [Fact]
    public async Task WrongRemoteProjectAndRequestedMergeRequestIdentityAreRejected()
    {
        var wrongProject = await Reader(new Handler(_ => Json("[{\"id\":1001,\"project_id\":999,\"iid\":7,\"title\":\"MR\",\"state\":\"opened\",\"draft\":false,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/7\"}]")))
            .DiscoverMergeRequestsAsync(Configuration(), new(), default);
        Assert.Equal(GitLabMetadataStatus.InvalidResponse, wrongProject.Status);

        var wrongIid = await Reader(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/approvals", StringComparison.Ordinal)
            ? Json("{\"approved_by\":[]}")
            : Json("{\"id\":1001,\"project_id\":86663796,\"iid\":8,\"title\":\"MR\",\"state\":\"opened\",\"draft\":false,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/8\"}")))
            .GetMergeRequestAsync(Configuration(), 7, default);
        Assert.Equal(GitLabMetadataStatus.InvalidResponse, wrongIid.Status);
    }

    [Fact]
    public async Task CommitResolutionUsesOnlyExactMetadataRoutesAndFallsBackFromBranchToTag()
    {
        var calls = new List<string>();
        var handler = new Handler(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/branches/release%2F1.6", StringComparison.Ordinal))
                return new(HttpStatusCode.NotFound);
            return Json($"{{\"commit\":{{\"id\":\"{Sha}\"}}}}");
        });
        var result = await Reader(handler).ResolveCommitAsync(Configuration(), "release/1.6", default);
        Assert.True(result.Succeeded);
        Assert.Equal(GitLabReferenceKind.Tag, result.Value!.ReferenceKind);
        Assert.Equal(Sha, result.Value.Sha);
        Assert.Equal(2, handler.Calls);
        Assert.All(calls, path => Assert.DoesNotContain("/raw/", path, StringComparison.Ordinal));

        var unsafeResult = await Reader(new Handler(_ => throw new Exception("No request permitted")))
            .ResolveCommitAsync(Configuration(), "../main", default);
        Assert.Equal(GitLabMetadataStatus.InvalidRequest, unsafeResult.Status);
    }

    [Fact]
    public async Task PinnedTreePreservesTypedEntriesAndKeysetContinuationWithoutReadingSource()
    {
        HttpRequestMessage? seen = null;
        var handler = new Handler(request =>
        {
            seen = request;
            return Json("[{\"id\":\"" + Sha + "\",\"name\":\"nav.c\",\"type\":\"blob\",\"path\":\"src/nav.c\",\"mode\":\"100644\"},{\"id\":\"" + Sha + "\",\"name\":\"vendor\",\"type\":\"commit\",\"path\":\"src/vendor\",\"mode\":\"160000\"},{\"id\":\"" + Sha + "\",\"name\":\"alias\",\"type\":\"blob\",\"path\":\"src/alias\",\"mode\":\"120000\"}]", ("Link", "<https://gitlab.example/api/v4/projects/86663796/repository/tree?pagination=keyset&per_page=4&page_token=next-token>; rel=\"next\""));
        });
        var result = await Reader(handler).ReadTreePageAsync(Configuration(), Sha, "src", null, 4, default);
        Assert.True(result.Succeeded);
        Assert.Equal(GitLabMetadataCompleteness.Partial, result.Completeness);
        Assert.Equal("next-token", result.Value!.NextCursor);
        Assert.Collection(result.Value.Entries,
            item => Assert.Equal(GitLabTreeEntryKind.Blob, item.Kind),
            item => Assert.Equal(GitLabTreeEntryKind.Commit, item.Kind),
            item => Assert.Equal(GitLabTreeEntryKind.Link, item.Kind));
        Assert.Contains("ref=" + Sha, seen!.RequestUri!.Query);
        Assert.Contains("path=src", seen.RequestUri.Query);
        Assert.DoesNotContain("/raw/", seen.RequestUri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalEmptyTreePageIsCompleteAndUnsafeInputsMakeNoCall()
    {
        var handler = new Handler(_ => Json("[]"));
        var result = await Reader(handler).ReadTreePageAsync(Configuration(), Sha, null, null, 20, default);
        Assert.Equal(GitLabMetadataCompleteness.Complete, result.Completeness);
        Assert.Null(result.Value!.NextCursor);
        var bad = await Reader(new Handler(_ => throw new Exception("No request permitted")))
            .ReadTreePageAsync(Configuration(), "abc", "../secret", null, 20, default);
        Assert.Equal(GitLabMetadataStatus.InvalidRequest, bad.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"draft\":null,")]
    [InlineData("\"draft\":\"true\",")]
    public async Task MalformedDraftIsNeverReportedAsNonDraft(string draft)
    {
        var row = "{\"id\":1001,\"project_id\":86663796,\"iid\":7,\"title\":\"MR\",\"state\":\"opened\"," + draft
            + "\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/7\"}";
        var list = await Reader(new Handler(_ => Json("[" + row + "]"))).DiscoverMergeRequestsAsync(Configuration(), new(), default);
        var detailHandler = new Handler(_ => Json(row));
        var detail = await Reader(detailHandler).GetMergeRequestAsync(Configuration(), 7, default);
        Assert.Equal(GitLabMetadataStatus.InvalidResponse, list.Status);
        Assert.Equal(GitLabMetadataStatus.InvalidResponse, detail.Status);
        Assert.Equal(1, detailHandler.Calls);
    }

    [Fact]
    public async Task InterruptedBodiesAreUnavailableAndDoNotDiscardUsableMergeRequestDetails()
    {
        static HttpResponseMessage Interrupted() => new(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream()) };
        var discovery = await Reader(new Handler(_ => Interrupted())).DiscoverMergeRequestsAsync(Configuration(), new(), default);
        Assert.Equal(GitLabMetadataStatus.ServiceUnavailable, discovery.Status);
        var detail = await Reader(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/approvals", StringComparison.Ordinal)
            ? Interrupted()
            : Json("{\"id\":1001,\"project_id\":86663796,\"iid\":7,\"title\":\"MR\",\"state\":\"opened\",\"draft\":false,\"web_url\":\"https://gitlab.example/group/project/-/merge_requests/7\"}")))
            .GetMergeRequestAsync(Configuration(), 7, default);
        Assert.True(detail.Succeeded);
        Assert.False(detail.Value!.Approvals.Known);
        Assert.Equal("service_unavailable", detail.Value.Approvals.Code);
    }

    [Fact]
    public async Task KeysetTraversalReachesTheTerminalPageWithoutChangingPinnedContext()
    {
        var handler = new Handler(request =>
        {
            Assert.Contains("ref=" + Sha, request.RequestUri!.Query);
            Assert.Contains("path=src", request.RequestUri.Query);
            var row = "[{\"id\":\"" + Sha + "\",\"name\":\"nav.c\",\"path\":\"src/nav.c\",\"type\":\"blob\",\"mode\":\"100644\"}]";
            return request.RequestUri.Query.Contains("page_token=next-token", StringComparison.Ordinal)
                ? Json(row)
                : Json(row, ("Link", "<https://gitlab.example/api/v4/projects/86663796/repository/tree?page_token=next-token>; rel=\"next\""));
        });
        var reader = Reader(handler);
        var first = await reader.ReadTreePageAsync(Configuration(), Sha, "src", null, 1, default);
        var last = await reader.ReadTreePageAsync(Configuration(), Sha, "src", first.NextPage, 1, default);
        Assert.Equal(GitLabMetadataCompleteness.Partial, first.Completeness);
        Assert.Equal(GitLabMetadataCompleteness.Complete, last.Completeness);
        Assert.Null(last.NextPage);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("<https://foreign.example/api/v4/projects/86663796/repository/tree?page_token=next>; rel=\"next\"")]
    [InlineData("<https://gitlab.example/api/v4/projects/999/repository/tree?page_token=next>; rel=\"next\"")]
    [InlineData("<https://gitlab.example/api/v4/projects/86663796/repository/tree>; rel=\"next\"")]
    [InlineData("malformed")]
    public async Task InvalidContinuationCannotMasqueradeAsTerminalPage(string link)
    {
        var result = await Reader(new Handler(_ => Json("[]", ("Link", link))))
            .ReadTreePageAsync(Configuration(), Sha, null, null, 20, default);
        Assert.Equal(GitLabMetadataStatus.InvalidResponse, result.Status);
        Assert.Null(result.Value);
    }

    private sealed class InterruptedStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new HttpIOException(HttpRequestError.ResponseEnded, "Response interrupted.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "Response interrupted."));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static GitLabMetadataReader Reader(Handler handler) => new(new HttpClient(handler),
        Options.Create(new ProjectGitLabOptions { BaseUrl = "https://gitlab.example", ReadAccessToken = "test-only-token" }));

    private static ProjectRepositoryConfiguration Configuration()
    {
        var configuration = new ProjectRepositoryConfiguration(Guid.NewGuid(), ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/group/project", "tester", DateTimeOffset.UtcNow);
        configuration.RecordVerification("tester", DateTimeOffset.UtcNow, 86663796, "group/project");
        return configuration;
    }

    private static HttpResponseMessage Json(string text, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var header in headers) response.Headers.TryAddWithoutValidation(header.Name, header.Value);
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response(request)); }
    }
}
