using AeroLink.Domain.Integrations;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class GitLabDisplayMetadataCacheTests
{
    [Fact]
    public async Task Coalescing_preserves_observation_time_and_isolates_caller_cancellation()
    {
        var clock = new Clock();
        var cache = new GitLabDisplayMetadataCache(clock);
        var ready = new TaskCompletionSource<GitLabMetadataResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<GitLabMetadataResult<string>> Read(CancellationToken ct) { Interlocked.Increment(ref calls); return ready.Task; }
        using var cancellation = new CancellationTokenSource();
        var abandoned = cache.ReadAsync("same", Read, cancellation.Token);
        var waiting = cache.ReadAsync("same", Read, default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        ready.SetResult(Success("metadata"));
        var observed = await waiting;
        clock.Advance(5);
        var cached = await cache.ReadAsync("same", Read, default);
        Assert.Equal(1, calls);
        Assert.True(cached.Reused);
        Assert.Equal(observed.ObservedAt, cached.ObservedAt);
        Assert.Equal(observed.ExpiresAt, cached.ExpiresAt);
        clock.Advance(11);
        var fresh = await cache.ReadAsync("same", Read, default);
        Assert.Equal(2, calls);
        Assert.False(fresh.Reused);
        Assert.True(fresh.ObservedAt > observed.ObservedAt);
    }

    [Fact]
    public async Task Failures_and_oversized_payloads_are_not_retained()
    {
        var cache = new GitLabDisplayMetadataCache();
        var calls = 0;
        Task<GitLabMetadataResult<string>> Failure(CancellationToken ct) { calls++; return Task.FromResult(new GitLabMetadataResult<string>(GitLabMetadataStatus.Forbidden, "forbidden", "Unavailable")); }
        await cache.ReadAsync("failed", Failure, default);
        await cache.ReadAsync("failed", Failure, default);
        Assert.Equal(2, calls);
        var oversized = new string('x', 8 * 1024 * 1024 + 1);
        Task<GitLabMetadataResult<string>> Large(CancellationToken ct) { calls++; return Task.FromResult(Success(oversized)); }
        await cache.ReadAsync("large", Large, default);
        await cache.ReadAsync("large", Large, default);
        Assert.Equal(4, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.ReadAsync<string>("faulted", _ => throw new InvalidOperationException(), default));
        var recovered = await cache.ReadAsync("faulted", _ => Task.FromResult(Success("recovered")), default);
        Assert.Equal("recovered", recovered.Observation.Value);
    }

    [Fact]
    public async Task Completed_capacity_evicts_oldest_observations()
    {
        var clock = new Clock();
        var cache = new GitLabDisplayMetadataCache(clock);
        var calls = 0;
        Task<GitLabMetadataResult<string>> Read(CancellationToken ct) { calls++; return Task.FromResult(Success("value")); }
        for (var index = 0; index < 129; index++) await cache.ReadAsync(index.ToString(), Read, default);
        await cache.ReadAsync("0", Read, default);
        Assert.Equal(130, calls);
    }

    [Fact]
    public async Task Inflight_capacity_bypasses_without_an_unbounded_pending_queue()
    {
        var cache = new GitLabDisplayMetadataCache();
        var gate = new TaskCompletionSource<GitLabMetadataResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<GitLabMetadataResult<string>> Read(CancellationToken ct) { calls++; return gate.Task; }
        var tasks = Enumerable.Range(0, 16).Select(index => cache.ReadAsync(index.ToString(), Read, default)).ToList();
        tasks.Add(cache.ReadAsync("overflow", Read, default));
        tasks.Add(cache.ReadAsync("overflow", Read, default));
        Assert.Equal(18, calls);
        gate.SetResult(Success("done"));
        await Task.WhenAll(tasks);
    }

    [Fact]
    public void Keys_separate_project_configuration_origin_credentials_and_case_sensitive_paths()
    {
        var configuration = new ProjectRepositoryConfiguration(Guid.NewGuid(), ProjectRepositorySetupMode.ConnectNow,
            "GitLab", "https://gitlab.example/group/repository", "tester", DateTimeOffset.UtcNow);
        var settings = new ProjectGitLabOptions { BaseUrl = "https://gitlab.example", ReadAccessToken = "test-secret" };
        string Key(string path) => GitLabDisplayMetadataCache.Key(configuration, settings, "tree", "sha", path, 1, 25);
        var first = Key("Source");
        Assert.NotEqual(first, Key("source"));
        Assert.DoesNotContain("test-secret", first);
        settings.ReadAccessToken = "rotated-test-secret";
        Assert.NotEqual(first, Key("Source"));
        settings.ReadAccessToken = "test-secret";
        settings.BaseUrl = "https://other.example";
        Assert.NotEqual(first, Key("Source"));
        settings.BaseUrl = "https://gitlab.example";
        configuration.RecordVerification("tester", DateTimeOffset.UtcNow, 17, "group/repository");
        Assert.NotEqual(first, Key("Source"));
    }

    private static GitLabMetadataResult<string> Success(string value) => new(GitLabMetadataStatus.Success, "ok", "Observed", value);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now += TimeSpan.FromSeconds(seconds);
    }
}
