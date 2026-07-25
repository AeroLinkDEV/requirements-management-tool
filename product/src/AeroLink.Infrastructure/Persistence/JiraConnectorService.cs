using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Content;
using AeroLink.Domain.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AeroLink.Infrastructure.Persistence;

public sealed record JiraProbeResult(bool Reachable, string Detail);
public sealed record JiraPushResult(bool Created, string IssueKey, string IssueUrl, string Detail);

/// <summary>
/// Talks to a Jira instance.
///
/// Behind an interface so the rest of the connector can be tested without a tracker, and so a deployment
/// that has not configured one is a visible, inspectable state rather than a silent hole — the same shape as
/// mail delivery, for the same reason.
/// </summary>
public interface IJiraClient
{
    Task<JiraProbeResult> ProbeAsync(JiraConnection connection, string apiToken, CancellationToken ct);
    Task<JiraPushResult> CreateIssueAsync(JiraConnection connection, string apiToken, string summary,
        string description, CancellationToken ct);
    Task<string?> ReadStatusAsync(JiraConnection connection, string apiToken, string issueKey, CancellationToken ct);
}

/// <summary>
/// The Jira REST client.
///
/// AeroLink is on-premises software and this is a server-side call to whatever Jira the organization already
/// runs — Cloud or Data Center. The browser never reaches Jira: an authored page that made an outbound
/// request would be a controlled tool phoning somewhere the deployment did not choose.
/// </summary>
public sealed class JiraClient(IHttpClientFactory factory, ILogger<JiraClient> logger) : IJiraClient
{
    public async Task<JiraProbeResult> ProbeAsync(JiraConnection connection, string apiToken, CancellationToken ct)
    {
        try
        {
            using var client = Client(connection, apiToken);
            // Asking for the project itself checks three things at once: the address resolves, the
            // credentials are accepted, and the project key exists. A bare /myself check would pass on a
            // connection whose project key is wrong, and the failure would surface later, on somebody's push.
            using var response = await client.GetAsync($"/rest/api/2/project/{Uri.EscapeDataString(connection.ProjectKey)}", ct);
            if (response.IsSuccessStatusCode)
                return new(true, $"{connection.ProjectKey} is reachable and the credentials were accepted.");
            return new(false, await DescribeAsync(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, $"The tracker could not be reached: {ex.Message}");
        }
    }

    public async Task<JiraPushResult> CreateIssueAsync(JiraConnection connection, string apiToken,
        string summary, string description, CancellationToken ct)
    {
        using var client = Client(connection, apiToken);
        var body = new
        {
            fields = new
            {
                project = new { key = connection.ProjectKey },
                issuetype = new { name = connection.IssueType },
                // Jira truncates a long summary server-side and returns a confusing error for some
                // configurations; trimming here keeps the failure out of the user's way.
                summary = summary.Length > 250 ? summary[..250] : summary,
                description,
            },
        };
        using var response = await client.PostAsJsonAsync("/rest/api/2/issue", body, ct);
        if (!response.IsSuccessStatusCode)
            return new(false, "", "", await DescribeAsync(response, ct));

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var key = payload.TryGetProperty("key", out var value) ? value.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(key))
            return new(false, "", "", "The tracker accepted the issue but did not return its key.");
        logger.LogInformation("Created Jira issue {IssueKey} in {ProjectKey}.", key, connection.ProjectKey);
        return new(true, key, $"{connection.BaseUrl}/browse/{key}", $"Created {key}.");
    }

    public async Task<string?> ReadStatusAsync(JiraConnection connection, string apiToken, string issueKey, CancellationToken ct)
    {
        try
        {
            using var client = Client(connection, apiToken);
            using var response = await client.GetAsync(
                $"/rest/api/2/issue/{Uri.EscapeDataString(issueKey)}?fields=status", ct);
            if (!response.IsSuccessStatusCode) return null;
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return payload.TryGetProperty("fields", out var fields) && fields.TryGetProperty("status", out var status)
                   && status.TryGetProperty("name", out var name)
                ? name.GetString()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private HttpClient Client(JiraConnection connection, string apiToken)
    {
        var client = factory.CreateClient("jira");
        client.BaseAddress = new Uri(connection.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(20);
        // Cloud authenticates with an email and an API token as basic credentials; Data Center uses a
        // personal access token as a bearer. Which one is in use is inferred from whether a user name was
        // configured, so an administrator does not have to know the distinction by name.
        client.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(connection.UserName)
            ? new AuthenticationHeaderValue("Bearer", apiToken)
            : new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.UserName}:{apiToken}")));
        return client;
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = body.Length > 400 ? body[..400] : body;
        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "The tracker rejected the credentials.",
            System.Net.HttpStatusCode.Forbidden => "The credentials are valid but not permitted to do this in that project.",
            System.Net.HttpStatusCode.NotFound => "The tracker has no such project or issue.",
            _ => $"The tracker responded {(int)response.StatusCode}. {detail}".Trim(),
        };
    }
}

/// <summary>
/// Pushes change requests to Jira and reflects what the tracker says back.
///
/// A push is deliberately not automatic. Not every change request is programme-tracked work, and creating an
/// issue for every draft would fill somebody's board with things that were never agreed. It is an act
/// somebody takes, attributed to them, and it happens once: a second push finds the existing link and does
/// nothing, because two issues for one change request is worse than none.
/// </summary>
public sealed class JiraConnectorService(AeroLinkDbContext db, IJiraClient client,
    IDataProtectionProvider dataProtection, IConfiguration configuration)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("AeroLink.Jira.ApiToken.v1");

    public string Protect(string token) => _protector.Protect(token);
    private string Unprotect(string protectedToken) => _protector.Unprotect(protectedToken);

    /// <summary>Where this deployment is reachable, so a Jira issue can point back at the record it came from.</summary>
    private string? BaseUrl
    {
        get
        {
            var configured = configuration["Notifications:BaseUrl"];
            return string.IsNullOrWhiteSpace(configured) ? null : configured.TrimEnd('/');
        }
    }

    public async Task<JiraProbeResult> VerifyAsync(JiraConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        var result = await client.ProbeAsync(connection, Unprotect(connection.ProtectedApiToken), ct);
        connection.RecordVerification(result.Reachable, result.Detail, now);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<JiraIssueLink> PushChangeRequestAsync(SystemChangeRequest scr, string actor,
        DateTimeOffset now, CancellationToken ct)
    {
        var connection = await db.JiraConnections
            .SingleOrDefaultAsync(x => x.ProjectId == scr.ProjectId && x.IsEnabled, ct)
            ?? throw new DomainException("This project has no enabled Jira connection.");

        // One issue per change request. A second push must not create a duplicate; it returns what already
        // exists, so pressing the button twice is harmless.
        var existing = await db.JiraIssueLinks
            .SingleOrDefaultAsync(x => x.ArtifactId == scr.Id && x.ArtifactType == "ChangeRequest", ct);
        if (existing is { State: JiraLinkState.Linked }) return existing;

        var link = existing ?? new JiraIssueLink(scr.ProjectId, connection.Id, "ChangeRequest", scr.Id, scr.DisplayNumber, actor, now);
        if (existing is null) db.JiraIssueLinks.Add(link);
        else link.Retry(now);

        JiraPushResult result;
        try
        {
            result = await client.CreateIssueAsync(connection, Unprotect(connection.ProtectedApiToken),
                $"{scr.DisplayNumber}: {scr.Title}", Describe(scr), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // An unreachable tracker is recorded on the link and nothing else fails. A change request must
            // never be blocked by a system that has no authority over it.
            result = new(false, "", "", $"The tracker could not be reached: {ex.Message}");
        }

        if (result.Created) link.RecordIssue(result.IssueKey, result.IssueUrl, now);
        else link.RecordFailure(result.Detail, now);
        await db.SaveChangesAsync(ct);
        return link;
    }

    /// <summary>
    /// Reads current status for the linked issues of a project. Returns how many were refreshed; a tracker
    /// that cannot be reached leaves the last known status in place rather than blanking it, because a stale
    /// status is information and an empty one is not.
    /// </summary>
    public async Task<int> RefreshStatusesAsync(Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        var connection = await db.JiraConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.IsEnabled, ct);
        if (connection is null) return 0;

        var links = await db.JiraIssueLinks
            .Where(x => x.ProjectId == projectId && x.State == JiraLinkState.Linked)
            .ToListAsync(ct);
        if (links.Count == 0) return 0;

        var token = Unprotect(connection.ProtectedApiToken);
        var refreshed = 0;
        foreach (var link in links)
        {
            var status = await client.ReadStatusAsync(connection, token, link.IssueKey, ct);
            if (status is null) continue;
            link.RecordStatus(status, now);
            refreshed++;
        }
        if (refreshed > 0) await db.SaveChangesAsync(ct);
        return refreshed;
    }

    /// <summary>
    /// The issue body. It carries the change case and a link back to the controlled record, because the
    /// point of the link is that somebody reading the Jira issue can reach the thing that is authoritative
    /// — and Jira is never that thing.
    /// </summary>
    internal string Describe(SystemChangeRequest scr)
    {
        var body = new StringBuilder();
        body.AppendLine($"h3. {scr.DisplayNumber}");
        body.AppendLine();
        body.AppendLine("*Problem*");
        body.AppendLine(RichContent.ToPlainText(scr.ProblemRich) is { Length: > 0 } problem ? problem : scr.Problem);
        body.AppendLine();
        body.AppendLine("*Analysis*");
        body.AppendLine(RichContent.ToPlainText(scr.AnalysisRich) is { Length: > 0 } analysis ? analysis : scr.Analysis);
        body.AppendLine();
        body.AppendLine("*Proposed solution*");
        body.AppendLine(RichContent.ToPlainText(scr.SolutionRich) is { Length: > 0 } solution ? solution : scr.Solution);
        body.AppendLine();
        body.AppendLine($"*Requirement changes:* {scr.RequirementChanges.Count}");
        body.AppendLine($"*State in AeroLink:* {scr.State}");
        body.AppendLine();

        var baseUrl = BaseUrl;
        if (baseUrl is not null)
        {
            body.AppendLine($"The controlled record is authoritative: {baseUrl}/systems/change-requests/{scr.Id}");
        }
        else
        {
            // Better to say why there is no link than to print a broken one.
            body.AppendLine("The controlled record in AeroLink is authoritative. (No public address is");
            body.AppendLine("configured for this deployment, so a direct link could not be included.)");
        }
        return body.ToString();
    }
}

/// <summary>
/// Reflects Jira status on a timer.
///
/// Nothing here is allowed to end the loop. A tracker that is down, a credential that expired, a project
/// that was renamed — each must leave the worker running, because the alternative is a status board that
/// silently stopped updating and nobody noticing until it mattered.
/// </summary>
public sealed class JiraStatusWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<JiraStatusWorker> logger) : BackgroundService
{
    private TimeSpan Interval =>
        int.TryParse(configuration["Jira:StatusPollSeconds"], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var connector = scope.ServiceProvider.GetRequiredService<JiraConnectorService>();
                var projects = await db.JiraConnections.AsNoTracking().Where(x => x.IsEnabled)
                    .Select(x => x.ProjectId).ToListAsync(stoppingToken);
                foreach (var projectId in projects)
                    await connector.RefreshStatusesAsync(projectId, DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Jira status reflection failed; the last known statuses are retained.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
