using System.Net;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AeroLink.Infrastructure;

namespace AeroLink.Infrastructure.Tests;

public sealed class GitLabProjectConnectionProbeTests
{
    [Fact]
    public void ProductionRegistrationDisablesRedirectsCookiesAndBoundsTimeout()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAeroLinkInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(GitLabProjectConnectionProbe));
        while (handler is DelegatingHandler wrapper) handler = wrapper.InnerHandler!;
        var transport = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseCookies);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GitLabProjectConnectionProbe));
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
    }

    [Theory]
    [InlineData("https://other.example/group/project")]
    [InlineData("https://user:secret@gitlab.example/group/project")]
    [InlineData("https://gitlab.example/group/project?private_token=secret")]
    [InlineData("http://gitlab.example/group/project")]
    public async Task UnapprovedDestinationsNeverReceiveTheInstallationCredential(string endpoint)
    {
        var handler = new Handler(_ => throw new Exception("No request permitted"));
        var result = await Probe(handler).ProbeAsync("GitLab", endpoint, default);
        Assert.False(result.Verified);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ExactObservedIdentityIsRequiredAndProbeIsReadOnly()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://gitlab.example/api/v4/projects/group%2Fproject", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-only-token", Assert.Single(request.Headers.GetValues("PRIVATE-TOKEN")));
            return Json("{\"id\":17,\"path_with_namespace\":\"group/project\",\"web_url\":\"https://gitlab.example/group/project\"}");
        });
        var result = await Probe(handler).ProbeAsync("GitLab", "https://gitlab.example/group/project", default);
        Assert.True(result.Verified);
        Assert.Equal(17, result.RemoteProjectId);
        Assert.Equal(1, handler.Calls);
        var wrong = new Handler(_ => Json("{\"id\":18,\"path_with_namespace\":\"group/other\",\"web_url\":\"https://gitlab.example/group/other\"}"));
        Assert.Equal("identity_mismatch", (await Probe(wrong).ProbeAsync("GitLab", "https://gitlab.example/group/project", default)).Code);
    }

    [Fact]
    public async Task UnconfiguredDeniedRedirectAndMalformedResponsesNeverClaimReadiness()
    {
        var unused = new Handler(_ => throw new Exception("No request permitted"));
        var service = new GitLabProjectConnectionProbe(new HttpClient(unused), Options.Create(new ProjectGitLabOptions()));
        Assert.Equal("service_unconfigured", (await service.ProbeAsync("GitLab", "https://gitlab.example/group/project", default)).Code);
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Redirect })
            Assert.False((await Probe(new Handler(_ => new(status))).ProbeAsync("GitLab", "https://gitlab.example/group/project", default)).Verified);
        Assert.False((await Probe(new Handler(_ => Json("not-json"))).ProbeAsync("GitLab", "https://gitlab.example/group/project", default)).Verified);
    }

    private static GitLabProjectConnectionProbe Probe(Handler handler) => new(new HttpClient(handler),
        Options.Create(new ProjectGitLabOptions { BaseUrl = "https://gitlab.example", ReadAccessToken = "test-only-token" }));
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response(request)); }
    }
}
