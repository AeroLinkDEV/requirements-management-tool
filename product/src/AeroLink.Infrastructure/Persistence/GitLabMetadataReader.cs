using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroLink.Domain.Integrations;
using Microsoft.Extensions.Options;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>States returned by the bounded, read-only GitLab metadata adapter.</summary>
public enum GitLabMetadataStatus
{
    Success,
    ServiceUnconfigured,
    RepositoryUnverified,
    InvalidConfiguration,
    InvalidRequest,
    Unauthorized,
    Forbidden,
    NotFound,
    RateLimited,
    Timeout,
    RedirectRejected,
    ServiceUnavailable,
    InvalidResponse
}

/// <summary>Whether a provider response establishes that the returned page is complete.</summary>
public enum GitLabMetadataCompleteness { Complete, Partial, Unknown }

public enum GitLabReferenceKind { Auto, Commit, Branch, Tag }

public sealed record GitLabMetadataResult<T>(
    GitLabMetadataStatus Status,
    string Code,
    string Detail,
    T? Value = default,
    GitLabMetadataCompleteness Completeness = GitLabMetadataCompleteness.Unknown,
    string? NextPage = null)
{
    public bool Succeeded => Status == GitLabMetadataStatus.Success;
}

public sealed record GitLabMergeRequestQuery(
    string? Search = null,
    string State = "all",
    int Page = 1,
    int PerPage = 20);

public sealed record GitLabApprovedBy(long Id, string Username, string Name);

/// <summary>Approval data is kept separate because the GitLab approvals endpoint may be unavailable.</summary>
public sealed record GitLabApprovalObservation(
    bool Known,
    IReadOnlyList<GitLabApprovedBy> ApprovedBy,
    string Code,
    string Detail);

public sealed record GitLabMergeRequestSummary(
    long Id,
    int Iid,
    string Title,
    string State,
    bool Draft,
    string WebUrl,
    string? Sha,
    string? MergeCommitSha,
    string? SquashMergeCommitSha,
    DateTimeOffset? MergedAt);

public sealed record GitLabMergeRequestDetails(
    long Id,
    long ProjectId,
    int Iid,
    string Title,
    string State,
    bool Draft,
    string WebUrl,
    string? SourceBranch,
    string? TargetBranch,
    string? Sha,
    string? MergeCommitSha,
    string? SquashMergeCommitSha,
    DateTimeOffset? MergedAt,
    GitLabApprovalObservation Approvals);

public sealed record GitLabCommitReference(
    long ProjectId,
    string RequestedReference,
    GitLabReferenceKind ReferenceKind,
    string Sha);

public enum GitLabTreeEntryKind { Blob, Tree, Commit, Link, Unknown }

public sealed record GitLabTreeEntry(
    string Path,
    string Name,
    GitLabTreeEntryKind Kind,
    string? Mode,
    string? Id);

public sealed record GitLabTreePage(
    long ProjectId,
    string CommitSha,
    string? RequestedPath,
    IReadOnlyList<GitLabTreeEntry> Entries,
    string? NextCursor);

/// <summary>
/// Reads only bounded GitLab metadata for the installation-approved, server-verified project.
/// This adapter deliberately does not read source, diffs, discussions, or repository contents.
/// </summary>
public sealed class GitLabMetadataReader(HttpClient client, IOptions<ProjectGitLabOptions> options)
{
    private const int MaxBodyBytes = 1024 * 1024;
    private const int MaxPageSize = 100;
    private const int MaxSearchLength = 200;
    private const int MaxReferenceLength = 256;
    private const int MaxPathLength = 2048;
    private static readonly TimeSpan WholeRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly Regex FullSha = new("^[0-9a-fA-F]{40}(?:[0-9a-fA-F]{24})?$", RegexOptions.CultureInvariant);

    public async Task<GitLabMetadataResult<IReadOnlyList<GitLabMergeRequestSummary>>> DiscoverMergeRequestsAsync(
        ProjectRepositoryConfiguration configuration, GitLabMergeRequestQuery? query, CancellationToken ct)
    {
        if (!TryCreateTarget(configuration, out var target, out var failure))
            return Failure<IReadOnlyList<GitLabMergeRequestSummary>>(failure);
        query ??= new();
        if (!TryValidateQuery(query, out var queryFailure))
            return Failure<IReadOnlyList<GitLabMergeRequestSummary>>(queryFailure);

        var queryParts = new List<string> { $"state={Uri.EscapeDataString(query.State.Trim().ToLowerInvariant())}", $"page={query.Page}", $"per_page={query.PerPage}" };
        if (!string.IsNullOrWhiteSpace(query.Search)) queryParts.Add($"search={Uri.EscapeDataString(query.Search.Trim())}");
        var response = await GetAsync(new Uri(target.ApiBase, $"projects/{target.ProjectId}/merge_requests?{string.Join('&', queryParts)}"), ct);
        if (!response.Ok) return Failure<IReadOnlyList<GitLabMergeRequestSummary>>(response);

        try
        {
            using var json = JsonDocument.Parse(response.Body!);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                return Invalid<IReadOnlyList<GitLabMergeRequestSummary>>("GitLab returned an unsupported merge-request response.");
            var rows = new List<GitLabMergeRequestSummary>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (!TryParseMergeRequest(item, target, out var row))
                    return Invalid<IReadOnlyList<GitLabMergeRequestSummary>>("GitLab returned an unsupported merge-request response.");
                rows.Add(row!);
                if (rows.Count > MaxPageSize)
                    return Invalid<IReadOnlyList<GitLabMergeRequestSummary>>("GitLab returned too many merge requests for one bounded page.");
            }
            var (completeness, next) = PageState(response.NextPage);
            return new(GitLabMetadataStatus.Success, "ok", "GitLab returned merge-request metadata.", rows, completeness, next);
        }
        catch (JsonException) { return Invalid<IReadOnlyList<GitLabMergeRequestSummary>>("GitLab returned an unsupported merge-request response."); }
        catch (InvalidOperationException) { return Invalid<IReadOnlyList<GitLabMergeRequestSummary>>("GitLab returned an unsupported merge-request response."); }
    }

    public async Task<GitLabMetadataResult<GitLabMergeRequestDetails>> GetMergeRequestAsync(
        ProjectRepositoryConfiguration configuration, int iid, CancellationToken ct)
    {
        if (!TryCreateTarget(configuration, out var target, out var failure))
            return Failure<GitLabMergeRequestDetails>(failure);
        if (iid <= 0)
            return Invalid<GitLabMergeRequestDetails>("The merge-request IID must be positive.");

        var response = await GetAsync(new Uri(target.ApiBase, $"projects/{target.ProjectId}/merge_requests/{iid}"), ct);
        if (!response.Ok) return Failure<GitLabMergeRequestDetails>(response);
        try
        {
            using var json = JsonDocument.Parse(response.Body!);
            if (!TryParseMergeRequestDetails(json.RootElement, target, iid, out var details))
                return Invalid<GitLabMergeRequestDetails>("GitLab returned an unsupported merge-request response.");

            var approvals = await ReadApprovalsAsync(target, iid, ct);
            details = details! with { Approvals = approvals };
            return new(GitLabMetadataStatus.Success, "ok", "GitLab returned merge-request metadata.", details);
        }
        catch (JsonException) { return Invalid<GitLabMergeRequestDetails>("GitLab returned an unsupported merge-request response."); }
        catch (InvalidOperationException) { return Invalid<GitLabMergeRequestDetails>("GitLab returned an unsupported merge-request response."); }
    }

    public async Task<GitLabMetadataResult<GitLabCommitReference>> ResolveCommitAsync(
        ProjectRepositoryConfiguration configuration, string reference, GitLabReferenceKind kind, CancellationToken ct)
    {
        if (!TryCreateTarget(configuration, out var target, out var failure))
            return Failure<GitLabCommitReference>(failure);
        if (!TryValidateReference(reference, out var normalizedReference))
            return InvalidRequest<GitLabCommitReference>("The source reference is empty, unsafe, or not bounded.");
        if (!Enum.IsDefined(kind)) return InvalidRequest<GitLabCommitReference>("The source reference kind is not supported.");

        var chosenKind = kind;
        if (kind == GitLabReferenceKind.Auto)
            chosenKind = FullSha.IsMatch(normalizedReference) ? GitLabReferenceKind.Commit : GitLabReferenceKind.Branch;

        if (chosenKind == GitLabReferenceKind.Commit)
            return await ResolveCommitObjectAsync(target, normalizedReference, GitLabReferenceKind.Commit, ct);

        var first = await ResolveRefObjectAsync(target, normalizedReference, chosenKind, ct);
        if (kind != GitLabReferenceKind.Auto || first.Status != GitLabMetadataStatus.NotFound)
            return first;
        return await ResolveRefObjectAsync(target, normalizedReference, GitLabReferenceKind.Tag, ct);
    }

    public Task<GitLabMetadataResult<GitLabCommitReference>> ResolveCommitAsync(
        ProjectRepositoryConfiguration configuration, string reference, CancellationToken ct) =>
        ResolveCommitAsync(configuration, reference, GitLabReferenceKind.Auto, ct);

    public async Task<GitLabMetadataResult<GitLabTreePage>> ReadTreePageAsync(
        ProjectRepositoryConfiguration configuration, string commitSha, string? path, string? cursor, int pageSize,
        CancellationToken ct)
    {
        if (!TryCreateTarget(configuration, out var target, out var failure))
            return Failure<GitLabTreePage>(failure);
        if (!FullSha.IsMatch(commitSha ?? ""))
            return InvalidRequest<GitLabTreePage>("The tree must be pinned to a full commit SHA.");
        if (!TryValidateTreePath(path, out var normalizedPath))
            return InvalidRequest<GitLabTreePage>("The tree path is unsafe or not bounded.");
        if (pageSize is < 1 or > MaxPageSize)
            return InvalidRequest<GitLabTreePage>("The tree page size must be between 1 and 100.");
        if (!string.IsNullOrEmpty(cursor) && !IsSafeCursor(cursor))
            return InvalidRequest<GitLabTreePage>("The tree continuation cursor is unsafe or not bounded.");

        var normalizedCommitSha = (commitSha ?? "").Trim().ToLowerInvariant();
        var parts = new List<string> { "pagination=keyset", $"ref={Uri.EscapeDataString(normalizedCommitSha)}", $"per_page={pageSize}" };
        if (!string.IsNullOrEmpty(normalizedPath)) parts.Add($"path={Uri.EscapeDataString(normalizedPath)}");
        if (!string.IsNullOrEmpty(cursor)) parts.Add($"page_token={Uri.EscapeDataString(cursor)}");
        var response = await GetAsync(new Uri(target.ApiBase, $"projects/{target.ProjectId}/repository/tree?{string.Join('&', parts)}"), ct);
        if (!response.Ok) return Failure<GitLabTreePage>(response);
        try
        {
            using var json = JsonDocument.Parse(response.Body!);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                return Invalid<GitLabTreePage>("GitLab returned an unsupported repository-tree response.");
            var entries = new List<GitLabTreeEntry>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (!TryParseTreeEntry(item, normalizedPath, out var entry))
                    return Invalid<GitLabTreePage>("GitLab returned an unsupported repository-tree response.");
                entries.Add(entry!);
                if (entries.Count > MaxPageSize)
                    return Invalid<GitLabTreePage>("GitLab returned too many tree entries for one bounded page.");
            }
            var (completeness, next) = PageState(response.NextCursor);
            var page = new GitLabTreePage(target.ProjectId, normalizedCommitSha, normalizedPath, entries, next);
            return new(GitLabMetadataStatus.Success, "ok", "GitLab returned a pinned repository-tree page.", page, completeness, next);
        }
        catch (JsonException) { return Invalid<GitLabTreePage>("GitLab returned an unsupported repository-tree response."); }
        catch (InvalidOperationException) { return Invalid<GitLabTreePage>("GitLab returned an unsupported repository-tree response."); }
    }

    private async Task<GitLabApprovalObservation> ReadApprovalsAsync(Target target, int iid, CancellationToken ct)
    {
        var response = await GetAsync(new Uri(target.ApiBase, $"projects/{target.ProjectId}/merge_requests/{iid}/approvals"), ct);
        if (!response.Ok)
            return new(false, [], response.Code, response.Detail);
        try
        {
            using var json = JsonDocument.Parse(response.Body!);
            if (!json.RootElement.TryGetProperty("approved_by", out var approvedBy)
                || approvedBy.ValueKind != JsonValueKind.Array)
                return new(false, [], "invalid_response", "GitLab did not provide an approved_by list.");
            var users = new List<GitLabApprovedBy>();
            foreach (var item in approvedBy.EnumerateArray())
            {
                if (!item.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object
                    || !user.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) || id <= 0)
                    return new(false, [], "invalid_response", "GitLab returned a malformed approved_by identity.");
                var username = StringProperty(user, "username", 120) ?? "";
                var name = StringProperty(user, "name", 240) ?? username;
                if (username.Length == 0 && name.Length == 0)
                    return new(false, [], "invalid_response", "GitLab returned a malformed approved_by identity.");
                users.Add(new(id, username, name));
                if (users.Count > MaxPageSize) return new(false, [], "invalid_response", "GitLab returned too many approval identities.");
            }
            return new(true, users, "ok", "GitLab returned the actual approved_by identities.");
        }
        catch (JsonException) { return new(false, [], "invalid_response", "GitLab returned an unsupported approvals response."); }
        catch (InvalidOperationException) { return new(false, [], "invalid_response", "GitLab returned an unsupported approvals response."); }
    }

    private async Task<GitLabMetadataResult<GitLabCommitReference>> ResolveRefObjectAsync(
        Target target, string reference, GitLabReferenceKind kind, CancellationToken ct)
    {
        var resource = kind == GitLabReferenceKind.Branch ? "branches" : "tags";
        var response = await GetAsync(new Uri(target.ApiBase, $"projects/{target.ProjectId}/repository/{resource}/{Uri.EscapeDataString(reference)}"), ct);
        if (!response.Ok) return Failure<GitLabCommitReference>(response);
        try
        {
            using var json = JsonDocument.Parse(response.Body!);
            if (!TryReadCommitSha(json.RootElement, out var sha))
                return Invalid<GitLabCommitReference>("GitLab returned an unsupported source-reference response.");
            return new(GitLabMetadataStatus.Success, "ok", "GitLab resolved the source reference to an exact commit.",
                new(target.ProjectId, reference, kind, sha!));
        }
        catch (JsonException) { return Invalid<GitLabCommitReference>("GitLab returned an unsupported source-reference response."); }
    }

    private async Task<GitLabMetadataResult<GitLabCommitReference>> ResolveCommitObjectAsync(
        Target target, string reference, GitLabReferenceKind kind, CancellationToken ct)
    {
        var response = await GetAsync(new Uri(target.ApiBase, $"projects/{target.ProjectId}/repository/commits/{Uri.EscapeDataString(reference)}"), ct);
        if (!response.Ok) return Failure<GitLabCommitReference>(response);
        try
        {
            using var json = JsonDocument.Parse(response.Body!);
            if (!TryReadCommitSha(json.RootElement, out var sha))
                return Invalid<GitLabCommitReference>("GitLab returned an unsupported commit response.");
            if (!string.Equals(sha, reference, StringComparison.OrdinalIgnoreCase))
                return Invalid<GitLabCommitReference>("GitLab returned a different commit identity than requested.");
            return new(GitLabMetadataStatus.Success, "ok", "GitLab confirmed the exact commit identity.",
                new(target.ProjectId, reference, kind, sha!));
        }
        catch (JsonException) { return Invalid<GitLabCommitReference>("GitLab returned an unsupported commit response."); }
        catch (InvalidOperationException) { return Invalid<GitLabCommitReference>("GitLab returned an unsupported commit response."); }
    }

    private async Task<TransportResponse> GetAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", options.Value.ReadAccessToken.Trim());
        request.Headers.Accept.ParseAdd("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WholeRequestTimeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
                return TransportResponse.Failure(GitLabMetadataStatus.RedirectRejected, "redirect_rejected", "GitLab returned a redirect, which is not followed for credential-bearing requests.");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return TransportResponse.Failure(GitLabMetadataStatus.Unauthorized, "unauthorized", "GitLab rejected the configured read credential.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return TransportResponse.Failure(GitLabMetadataStatus.Forbidden, "forbidden", "GitLab denied access to the configured project.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return TransportResponse.Failure(GitLabMetadataStatus.NotFound, "not_found", "GitLab could not find the requested metadata.");
            if (response.StatusCode == (HttpStatusCode)429)
                return TransportResponse.Failure(GitLabMetadataStatus.RateLimited, "rate_limited", "GitLab rate-limited the metadata request.");
            if (!response.IsSuccessStatusCode)
                return TransportResponse.Failure(GitLabMetadataStatus.ServiceUnavailable, "service_unavailable", "GitLab did not return a successful metadata response.");
            if (response.Content.Headers.ContentLength > MaxBodyBytes)
                return TransportResponse.Failure(GitLabMetadataStatus.InvalidResponse, "response_too_large", "GitLab returned a response larger than the bounded metadata limit.");
            var body = await ReadBoundedBodyAsync(response.Content, timeout.Token);
            if (body is null)
                return TransportResponse.Failure(GitLabMetadataStatus.InvalidResponse, "response_too_large", "GitLab returned a response larger than the bounded metadata limit.");
            response.Headers.TryGetValues("X-Next-Page", out var nextPageValues);
            string? nextCursor = null;
            if (uri.AbsolutePath.EndsWith("/repository/tree", StringComparison.Ordinal)
                && !TryExtractTreeCursor(response.Headers, uri, out nextCursor))
                return TransportResponse.Failure(GitLabMetadataStatus.InvalidResponse, "invalid_response", "GitLab returned an invalid tree continuation.");
            return TransportResponse.Success(body, FirstHeader(nextPageValues), nextCursor);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return TransportResponse.Failure(GitLabMetadataStatus.Timeout, "timeout", "GitLab did not respond in time."); }
        catch (HttpRequestException)
        { return TransportResponse.Failure(GitLabMetadataStatus.ServiceUnavailable, "service_unavailable", "The GitLab metadata connection is unavailable."); }
        catch (IOException)
        { return TransportResponse.Failure(GitLabMetadataStatus.ServiceUnavailable, "service_unavailable", "The GitLab metadata response was interrupted."); }
        catch (InvalidOperationException)
        { return TransportResponse.Failure(GitLabMetadataStatus.InvalidResponse, "invalid_response", "GitLab returned an unsupported metadata response."); }
    }

    private static async Task<string?> ReadBoundedBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > MaxBodyBytes) return null;
            body.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private bool TryCreateTarget(ProjectRepositoryConfiguration configuration, out Target target,
        out TransportResponse failure)
    {
        target = null!;
        if (configuration is null)
        {
            failure = TransportResponse.Failure(GitLabMetadataStatus.InvalidConfiguration, "invalid_configuration", "A verified GitLab repository configuration is required.");
            return false;
        }
        var settings = options.Value;
        if (!TryCreateServer(settings.BaseUrl, out var server))
        {
            failure = TransportResponse.Failure(GitLabMetadataStatus.ServiceUnconfigured, "service_unconfigured", "The installation has no valid configured GitLab server.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(settings.ReadAccessToken))
        {
            failure = TransportResponse.Failure(GitLabMetadataStatus.ServiceUnconfigured, "service_unconfigured", "The installation has no configured GitLab read connection.");
            return false;
        }
        if (!string.Equals(configuration.Provider, "GitLab", StringComparison.OrdinalIgnoreCase))
        {
            failure = TransportResponse.Failure(GitLabMetadataStatus.InvalidConfiguration, "unsupported_provider", "The repository configuration is not GitLab.");
            return false;
        }
        if (configuration.Status != ProjectRepositorySetupStatus.Verified || configuration.RemoteProjectId is not > 0
            || string.IsNullOrWhiteSpace(configuration.RemotePathWithNamespace))
        {
            failure = TransportResponse.Failure(GitLabMetadataStatus.RepositoryUnverified, "repository_unverified", "The GitLab repository identity has not been server-verified.");
            return false;
        }
        if (!TryNormalizeEndpoint(configuration.Endpoint, server!, out var path)
            || !string.Equals(path, configuration.RemotePathWithNamespace, StringComparison.Ordinal))
        {
            failure = TransportResponse.Failure(GitLabMetadataStatus.InvalidConfiguration, "identity_mismatch", "The configured endpoint does not match the server-observed GitLab repository identity.");
            return false;
        }
        target = new(server!, configuration.RemoteProjectId.Value, path!);
        failure = default!;
        return true;
    }

    private static bool TryCreateServer(string? value, out Uri? server)
    {
        server = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        if (path.Contains("//", StringComparison.Ordinal) || !SafeSegments(path)) return false;
        server = uri;
        return true;
    }

    private static bool TryNormalizeEndpoint(string? endpoint, Uri server, out string? path)
    {
        path = null;
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttps
            || target.UserInfo.Length != 0 || target.Query.Length != 0 || target.Fragment.Length != 0 || !SameOrigin(target, server)) return false;
        var basePath = Uri.UnescapeDataString(server.AbsolutePath).TrimEnd('/');
        var targetPath = Uri.UnescapeDataString(target.AbsolutePath).TrimEnd('/');
        var prefix = basePath.Length == 0 ? "/" : basePath + "/";
        if (!targetPath.StartsWith(prefix, StringComparison.Ordinal) || !SafeSegments(targetPath)) return false;
        path = targetPath[prefix.Length..].Trim('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        return path.Split('/').Length >= 2 && SafeSegments(path);
    }

    private static bool SameOrigin(Uri left, Uri right) => left.Scheme == Uri.UriSchemeHttps && right.Scheme == Uri.UriSchemeHttps
        && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private static bool SafeSegments(string value) => value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).All(segment => segment.Length > 0 && segment is not "." and not ".."
        && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));

    private static bool TryValidateQuery(GitLabMergeRequestQuery query, out TransportResponse failure)
    {
        failure = default!;
        if (query.Page < 1 || query.PerPage is < 1 or > MaxPageSize || (query.Search?.Length ?? 0) > MaxSearchLength
            || (query.Search?.Any(char.IsControl) ?? false))
        { failure = TransportResponse.Failure(GitLabMetadataStatus.InvalidRequest, "invalid_request", "The merge-request query is outside the bounded read contract."); return false; }
        var state = query.State?.Trim().ToLowerInvariant() ?? "";
        if (state is not ("all" or "opened" or "closed" or "merged"))
        { failure = TransportResponse.Failure(GitLabMetadataStatus.InvalidRequest, "invalid_request", "The merge-request state filter is not supported."); return false; }
        return true;
    }

    private static bool TryValidateReference(string value, out string normalized)
    {
        normalized = value?.Trim() ?? "";
        return normalized.Length is > 0 and <= MaxReferenceLength && !normalized.Any(char.IsControl)
            && !normalized.Contains('\\') && !normalized.StartsWith('/') && !normalized.EndsWith('/')
            && !normalized.Contains("//", StringComparison.Ordinal)
            && normalized.Split('/').All(x => x is not "." and not ".." && x.Length > 0);
    }

    private static bool TryValidateTreePath(string? value, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim('/');
        if (normalized is null) return true;
        return normalized.Length <= MaxPathLength && !normalized.Any(char.IsControl) && !normalized.Contains('\\')
            && normalized.Split('/').All(x => x.Length > 0 && x is not "." and not "..");
    }

    private static bool IsSafeCursor(string cursor) => cursor.Length <= MaxReferenceLength && !cursor.Any(char.IsControl)
        && !cursor.Contains('\\') && !cursor.Contains('?') && !cursor.Contains('#');

    private static bool TryParseMergeRequest(JsonElement item, Target target, out GitLabMergeRequestSummary? value)
    {
        value = null;
        if (item.ValueKind != JsonValueKind.Object || !TryReadPositiveLong(item, "id", out var id)
            || !TryReadPositiveLong(item, "project_id", out var projectId) || projectId != target.ProjectId
            || !TryReadPositiveInt(item, "iid", out var iid)) return false;
        var title = StringProperty(item, "title", 500); var state = StringProperty(item, "state", 32);
        var web = StringProperty(item, "web_url", 2000);
        if (title is null || state is null || web is null || !IsMergeRequestUrl(web, target, iid)
            || !TryReadBoolean(item, "draft", out var draft)) return false;
        var sha = OptionalSha(item, "sha"); var mergeSha = OptionalSha(item, "merge_commit_sha"); var squashSha = OptionalSha(item, "squash_merge_commit_sha");
        if (sha.Invalid || mergeSha.Invalid || squashSha.Invalid) return false;
        value = new(id, iid, title, state, draft, web,
            sha.Value, mergeSha.Value, squashSha.Value, DateProperty(item, "merged_at"));
        return true;
    }

    private static bool TryParseMergeRequestDetails(JsonElement item, Target target, int requestedIid, out GitLabMergeRequestDetails? value)
    {
        value = null;
        if (item.ValueKind != JsonValueKind.Object || !TryReadPositiveLong(item, "id", out var id) || !TryReadPositiveLong(item, "project_id", out var projectId)
            || projectId != target.ProjectId || !TryReadPositiveInt(item, "iid", out var iid) || iid != requestedIid) return false;
        var title = StringProperty(item, "title", 500); var state = StringProperty(item, "state", 32); var web = StringProperty(item, "web_url", 2000);
        if (title is null || state is null || web is null || projectId != target.ProjectId || !IsMergeRequestUrl(web, target, iid)
            || !TryReadBoolean(item, "draft", out var draft)) return false;
        var sha = OptionalSha(item, "sha"); var mergeSha = OptionalSha(item, "merge_commit_sha"); var squashSha = OptionalSha(item, "squash_merge_commit_sha");
        if (sha.Invalid || mergeSha.Invalid || squashSha.Invalid) return false;
        value = new(id, projectId, iid, title, state, draft, web,
            StringProperty(item, "source_branch", 256), StringProperty(item, "target_branch", 256),
            sha.Value, mergeSha.Value, squashSha.Value, DateProperty(item, "merged_at"),
            new(false, [], "unknown", "Approval data has not been read."));
        return true;
    }

    private static bool IsMergeRequestUrl(string value, Target target, int iid)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !SameOrigin(uri, target.Server) || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var expected = Uri.UnescapeDataString(target.Server.AbsolutePath).TrimEnd('/') + "/" + target.Path + "/-/merge_requests/" + iid;
        return string.Equals(Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/'), expected, StringComparison.Ordinal);
    }

    private static bool TryParseTreeEntry(JsonElement item, string? requestedPath, out GitLabTreeEntry? value)
    {
        value = null;
        if (item.ValueKind != JsonValueKind.Object) return false;
        var path = StringProperty(item, "path", MaxPathLength); var name = StringProperty(item, "name", 500); var type = StringProperty(item, "type", 32);
        if (path is null || name is null || type is null || !TryValidateTreePath(path, out var safe) || safe is null) return false;
        var mode = StringProperty(item, "mode", 32);
        var expectedPrefix = string.IsNullOrEmpty(requestedPath) ? "" : requestedPath + "/";
        var relative = safe.StartsWith(expectedPrefix, StringComparison.Ordinal) ? safe[expectedPrefix.Length..] : "";
        if (relative.Length == 0 || relative.Contains('/') || !string.Equals(relative, name, StringComparison.Ordinal)) return false;
        var kind = mode switch
        {
            "120000" => GitLabTreeEntryKind.Link,
            "160000" => GitLabTreeEntryKind.Commit,
            _ => type switch { "blob" => GitLabTreeEntryKind.Blob, "tree" => GitLabTreeEntryKind.Tree, "commit" => GitLabTreeEntryKind.Commit, _ => GitLabTreeEntryKind.Unknown }
        };
        var id = OptionalSha(item, "id");
        if (id.Invalid) return false;
        value = new(safe, name, kind, mode, id.Value);
        return true;
    }

    private static bool TryReadCommitSha(JsonElement root, out string? sha)
    {
        sha = null;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object) root = commit;
        var value = StringProperty(root, "id", 64);
        if (value is null || !FullSha.IsMatch(value)) return false;
        sha = value.ToLowerInvariant(); return true;
    }

    private static bool TryReadPositiveLong(JsonElement item, string property, out long value)
    {
        value = 0;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var element) && element.TryGetInt64(out value) && value > 0;
    }

    private static bool TryReadPositiveInt(JsonElement item, string property, out int value)
    {
        value = 0;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var element) && element.TryGetInt32(out value) && value > 0;
    }
    private static bool TryReadBoolean(JsonElement item, string property, out bool result)
    {
        result = false;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = value.GetBoolean();
        return true;
    }
    private static string? StringProperty(JsonElement item, string property, int max)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is not null && text.Length <= max && !text.Any(char.IsControl) ? text : null;
    }
    private static (string? Value, bool Invalid) OptionalSha(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return (null, false);
        if (value.ValueKind != JsonValueKind.String) return (null, true);
        var sha = value.GetString();
        return (sha is not null && FullSha.IsMatch(sha) ? sha.ToLowerInvariant() : null, sha is null || !FullSha.IsMatch(sha));
    }
    private static DateTimeOffset? DateProperty(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var result) ? result : null;

    private static (GitLabMetadataCompleteness Completeness, string? Next) PageState(string? next)
    {
        if (next is null) return (GitLabMetadataCompleteness.Unknown, null);
        if (string.IsNullOrWhiteSpace(next) || next == "0") return (GitLabMetadataCompleteness.Complete, null);
        return (GitLabMetadataCompleteness.Partial, next);
    }

    private static string? FirstHeader(IEnumerable<string>? values) => values?.FirstOrDefault();

    private static bool TryExtractTreeCursor(HttpResponseHeaders headers, Uri requestUri, out string? cursor)
    {
        // GitLab's keyset pager omits Link on a terminal page. Invalid continuation is a failed
        // observation, not a terminal page; never follow a server-supplied URL with credentials.
        cursor = "";
        if (!headers.TryGetValues("Link", out var links)) return true;
        var sawNext = false;
        foreach (var link in links.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries)))
        {
            var open = link.IndexOf('<'); var close = link.IndexOf('>');
            if (open != 0 || close <= open) return false;
            var relation = Regex.Match(link[(close + 1)..], "(?:^|;)\\s*rel=(?:\"(?<rel>[^\"]+)\"|(?<rel>[^;\\s]+))(?=;|$)", RegexOptions.IgnoreCase);
            if (!relation.Success) return false;
            if (!relation.Groups["rel"].Value.Split(' ').Contains("next", StringComparer.OrdinalIgnoreCase)) continue;
            if (sawNext) return false;
            sawNext = true;
            var candidate = link[(open + 1)..close];
            if (!Uri.TryCreate(requestUri, candidate, out var next) || !SameOrigin(next, requestUri)
                || next.UserInfo.Length != 0 || next.Fragment.Length != 0
                || !string.Equals(next.AbsolutePath, requestUri.AbsolutePath, StringComparison.Ordinal)) return false;
            var query = next.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            var tokens = new List<string>();
            foreach (var pair in query)
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0 || !string.Equals(Uri.UnescapeDataString(pair[..separator]), "page_token", StringComparison.Ordinal)) continue;
                tokens.Add(Uri.UnescapeDataString(pair[(separator + 1)..]));
            }
            if (tokens.Count != 1 || tokens[0].Length == 0 || !IsSafeCursor(tokens[0])) return false;
            cursor = tokens[0];
        }
        return true;
    }
    private static GitLabMetadataResult<T> Failure<T>(TransportResponse response) => new(response.Status, response.Code, response.Detail);
    private static GitLabMetadataResult<T> Invalid<T>(string detail) => new(GitLabMetadataStatus.InvalidResponse, "invalid_response", detail);
    private static GitLabMetadataResult<T> InvalidRequest<T>(string detail) => new(GitLabMetadataStatus.InvalidRequest, "invalid_request", detail);

    private sealed record Target(Uri Server, long ProjectId, string Path)
    {
        public Uri ApiBase => new(Server.GetLeftPart(UriPartial.Authority) + Uri.UnescapeDataString(Server.AbsolutePath).TrimEnd('/') + "/api/v4/");
    }

    private sealed record TransportResponse(bool Ok, GitLabMetadataStatus Status, string Code, string Detail,
        string? Body = null, string? NextPage = null, string? NextCursor = null)
    {
        public static TransportResponse Success(string body, string? nextPage, string? nextCursor) => new(true, GitLabMetadataStatus.Success, "ok", "", body, nextPage, nextCursor);
        public static TransportResponse Failure(GitLabMetadataStatus status, string code, string detail) => new(false, status, code, detail);
    }
}
