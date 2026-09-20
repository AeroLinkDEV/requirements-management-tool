using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Integrations;

namespace AeroLink.Infrastructure.Persistence;

public sealed record GitLabDisplayObservation<T>(GitLabMetadataResult<T> Observation,
    DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt, bool Reused);

/// <summary>Short-lived display observations only. Commands must call GitLabMetadataReader directly.</summary>
public sealed class GitLabDisplayMetadataCache(TimeProvider? timeProvider = null)
{
    private const int MaximumEntries = 128, MaximumPending = 16, MaximumBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<object>> pending = new(StringComparer.Ordinal);
    private int retainedBytes;
    private sealed record Entry(object Value, DateTimeOffset ExpiresAt, int Bytes);

    public static string Key(ProjectRepositoryConfiguration configuration, ProjectGitLabOptions settings,
        string operation, params object?[] arguments) => JsonSerializer.Serialize(new {
            configuration.ProjectId, configuration.Id, configuration.Version, configuration.Status,
            configuration.Endpoint, configuration.RemoteProjectId, configuration.RemotePathWithNamespace,
            settings.BaseUrl, accessIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ReadAccessToken ?? ""))),
            operation, arguments });

    public async Task<GitLabDisplayObservation<T>> ReadAsync<T>(string key,
        Func<CancellationToken, Task<GitLabMetadataResult<T>>> observe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        key = typeof(T).FullName + ":" + key;
        Task<object>? shared;
        var reused = false;
        TaskCompletionSource<object>? owner = null;
        lock (gate)
        {
            foreach (var expired in entries.Where(x => x.Value.ExpiresAt <= clock.GetUtcNow()).Select(x => x.Key).ToArray()) Remove(expired);
            if (entries.TryGetValue(key, out var entry))
                return ((GitLabDisplayObservation<T>)entry.Value) with { Reused = true };
            if (pending.TryGetValue(key, out shared)) reused = true;
            else if (pending.Count < MaximumPending)
            {
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                shared = owner.Task;
                pending.Add(key, shared);
            }
        }
        // Capacity overflow is an ordinary uncached read, never an unbounded queue in the cache.
        if (shared is null) return Wrap(await observe(ct));
        if (owner is not null) _ = PopulateAsync(key, observe, owner);
        return ((GitLabDisplayObservation<T>)await shared.WaitAsync(ct)) with { Reused = reused };
    }

    private GitLabDisplayObservation<T> Wrap<T>(GitLabMetadataResult<T> result)
    {
        var now = clock.GetUtcNow();
        return new(result, now, now + Lifetime, false);
    }

    private async Task PopulateAsync<T>(string key, Func<CancellationToken, Task<GitLabMetadataResult<T>>> observe,
        TaskCompletionSource<object> completion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        GitLabDisplayObservation<T>? observed = null;
        Exception? failure = null;
        try
        {
            // One caller abandoning its page must not cancel another caller's shared observation.
            // The reader also has its own bounded transport timeout; this bounds the entire shared call.
            observed = Wrap(await observe(timeout.Token));
            if (observed.Observation.Succeeded)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(observed).Length;
                if (bytes <= MaximumBytes)
                {
                    lock (gate)
                    {
                        while (entries.Count >= MaximumEntries || retainedBytes + bytes > MaximumBytes)
                            Remove(entries.MinBy(x => x.Value.ExpiresAt).Key);
                        entries[key] = new(observed, observed.ExpiresAt, bytes);
                        retainedBytes += bytes;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            observed = Wrap(new GitLabMetadataResult<T>(GitLabMetadataStatus.Timeout,
                "metadata_timeout", "GitLab metadata did not complete within the display observation window."));
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (gate)
            {
                // Remove the completed operation before waking waiters. Otherwise a caller can advance
                // past the observation expiry after completion is signaled but before this cleanup runs,
                // then reuse an already-completed pending task instead of starting a fresh observation.
                pending.Remove(key);
                if (failure is not null)
                    completion.TrySetException(failure);
                else
                    completion.TrySetResult(observed!);
            }

            if (failure is not null)
            {
                // All waiters may have navigated away; observe the fault without retaining a poisoned entry.
                _ = completion.Task.Exception;
            }
        }
    }

    private void Remove(string key)
    {
        if (entries.Remove(key, out var entry)) retainedBytes -= entry.Bytes;
    }
}
