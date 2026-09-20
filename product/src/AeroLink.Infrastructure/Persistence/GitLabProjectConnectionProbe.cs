using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AeroLink.Infrastructure.Persistence;

public sealed class ProjectGitLabOptions
{
    public string BaseUrl { get; set; } = "";
    public string ReadAccessToken { get; set; } = "";
    public string SyntheticDemoProjectId { get; set; } = "";
    public string SyntheticDemoRemoteProjectId { get; set; } = "";
}

public sealed record ProjectRepositoryProbeResult(bool Verified, string Code, string Detail,
    long? RemoteProjectId = null, string? RemotePath = null);

/// <summary>Observes one existing GitLab project. It never creates repository content or sends mutations.</summary>
public sealed class GitLabProjectConnectionProbe(HttpClient client, IOptions<ProjectGitLabOptions> options)
{
    public async Task<ProjectRepositoryProbeResult> ProbeAsync(string? provider, string? endpoint, CancellationToken ct)
    {
        if (!string.Equals(provider, "GitLab", StringComparison.OrdinalIgnoreCase))
            return Failed("unsupported_provider", "Choose the supported GitLab provider.");
        var settings = options.Value;
        if (!TryHttps(settings.BaseUrl, out var server) || string.IsNullOrWhiteSpace(settings.ReadAccessToken))
            return Failed("service_unconfigured", "The installation has no configured GitLab read connection. Repository setup remains pending verification.");
        if (!TryHttps(endpoint, out var target) || target!.Authority != server!.Authority)
            return Failed("unapproved_server", "The repository must belong to the installation's configured GitLab server.");
        var basePath = server!.AbsolutePath.TrimEnd('/') + "/";
        if (!target!.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal))
            return Failed("unapproved_server", "The repository must belong to the configured GitLab server path.");
        var path = Uri.UnescapeDataString(target.AbsolutePath[basePath.Length..].TrimEnd('/'));
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        if (path.Split('/').Length < 2 || path.Split('/').Any(p => p.Length == 0 || p is "." or ".."
            || p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))))
            return Failed("invalid_repository", "Enter the GitLab project URL, including its namespace and project path.");
        var api = new Uri(server.GetLeftPart(UriPartial.Authority) + basePath + "api/v4/projects/" + Uri.EscapeDataString(path));
        using var request = new HttpRequestMessage(HttpMethod.Get, api);
        request.Headers.Add("PRIVATE-TOKEN", settings.ReadAccessToken);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failed("access_denied", "The installation's GitLab connection cannot read this project.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Failed("repository_unavailable", "The repository was not found or is unavailable to the installation's GitLab connection.");
            if (!response.IsSuccessStatusCode)
                return Failed("service_unavailable", "GitLab did not return a successful project response. Setup remains unverified.");
            if (response.Content.Headers.ContentLength > 1024 * 1024)
                return Failed("invalid_response", "GitLab returned an unsupported project response.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > 1024 * 1024) return Failed("invalid_response", "GitLab returned an unsupported project response.");
                body.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(body.ToArray());
            var root = json.RootElement;
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt64(out var projectId) || projectId <= 0
                || !root.TryGetProperty("path_with_namespace", out var observedPath) || observedPath.GetString() != path
                || !root.TryGetProperty("web_url", out var web) || !TryHttps(web.GetString(), out var observedUrl)
                || observedUrl!.Authority != server.Authority
                || Uri.UnescapeDataString(observedUrl.AbsolutePath.TrimEnd('/')) != Uri.UnescapeDataString(basePath + path))
                return Failed("identity_mismatch", "GitLab returned a different repository identity. Review the URL before retrying.");
            return new(true, "verified", "GitLab confirmed read access to this exact project.", projectId, path);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Failed("service_timeout", "GitLab did not respond in time. Setup remains unverified."); }
        catch (HttpRequestException)
        { return Failed("service_unavailable", "The GitLab connection is unavailable. Setup remains unverified."); }
        catch (JsonException)
        { return Failed("invalid_response", "GitLab returned an unsupported project response."); }
        catch (InvalidOperationException)
        { return Failed("invalid_response", "GitLab returned an unsupported project response."); }
    }

    private static ProjectRepositoryProbeResult Failed(string code, string detail) => new(false, code, detail);
    private static bool TryHttps(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
