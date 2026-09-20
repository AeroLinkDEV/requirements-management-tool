using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Commits the local half of source confirmation. The API must authorize the actor and obtain the
/// observation from GitLab before acquiring the scope; this service never performs remote I/O.
/// Caller owns SaveChanges and commit so audit and selection are one transaction.
/// </summary>
public sealed class GitLabSourceSelectionService(AeroLinkDbContext db)
{
    public async Task<GitLabSourceSelectionEvent> SelectAsync(ProjectControlledWriteScope scope,
        Guid releaseId, ProjectRepositoryConfiguration observedConfiguration, string instanceBaseUrl,
        GitLabCommitReference observation, string expectedPreviewSha, long expectedSelectionVersion,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        ProjectControlledWriteScope.Require(db, observedConfiguration.ProjectId, scope);
        var projectId = scope.ProjectId;
        if (expectedSelectionVersion < 0)
            throw new DomainException("The expected source selection version cannot be negative.");
        if (!string.Equals(observation.Sha, expectedPreviewSha, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The source reference moved since preview. Preview the new commit before confirming it.");

        var configuration = await db.ProjectRepositoryConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (configuration is null || configuration.Id != observedConfiguration.Id
            || configuration.Version != observedConfiguration.Version
            || configuration.Status != ProjectRepositorySetupStatus.Verified
            || configuration.RemoteProjectId != observation.ProjectId
            || configuration.RemoteProjectId != observedConfiguration.RemoteProjectId
            || configuration.RemotePathWithNamespace != observedConfiguration.RemotePathWithNamespace
            || configuration.Endpoint != observedConfiguration.Endpoint)
            throw new DomainException("Repository configuration changed during source confirmation. Refresh before retrying.");

        var release = await db.Releases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct);
        if (release is null) throw new DomainException("The selected build does not belong to this Project.");
        if (release.IsReleased || await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x =>
                x.ProjectId == projectId && x.ReleaseId == releaseId
                && (x.State == ReleaseCampaignState.InReview || x.State == ReleaseCampaignState.Released), ct))
            throw new DomainException("The build is frozen or released and its source selection cannot change.");

        // Tracked pointer is first loaded after acquisition of the project lock. Do not infer current
        // selection from event timestamps, and do not revive evidence selected under an older event.
        if (db.ChangeTracker.Entries<GitLabCurrentSourceSelection>().Any(x =>
                x.Entity.ProjectId == projectId && x.Entity.ReleaseId == releaseId))
            throw new InvalidOperationException("Load the source pointer only inside this source-selection operation.");
        var current = await db.GitLabCurrentSourceSelections
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.ReleaseId == releaseId, ct);
        if ((current?.Version ?? 0) != expectedSelectionVersion)
            throw new DomainException("Another source selection was saved. Refresh before changing it.");

        var snapshot = new GitLabSourceSnapshot(projectId, configuration.Id, instanceBaseUrl,
            observation.ProjectId, configuration.RemotePathWithNamespace!, observation.Sha,
            observation.RequestedReference, actor, now, configuration.Version);
        if (!MatchesRepositoryInstance(configuration, snapshot))
            throw new DomainException("The source observation does not belong to the configured GitLab repository instance.");
        var selection = new GitLabSourceSelectionEvent(projectId, releaseId, snapshot.Id,
            expectedSelectionVersion, actor, now);
        if (current is null)
            db.GitLabCurrentSourceSelections.Add(new(projectId, releaseId, snapshot.Id, selection.Id, actor, now));
        else
            current.Move(expectedSelectionVersion, snapshot.Id, selection.Id, actor, now);
        db.GitLabSourceSnapshots.Add(snapshot);
        db.GitLabSourceSelectionEvents.Add(selection);
        return selection;
    }

    private static bool MatchesRepositoryInstance(ProjectRepositoryConfiguration configuration, GitLabSourceSnapshot snapshot)
    {
        if (!string.Equals(configuration.Provider, "GitLab", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out var endpoint)) return false;
        var instance = new Uri(snapshot.InstanceBaseUrl);
        if (endpoint.Scheme != Uri.UriSchemeHttps
            || !string.Equals(endpoint.IdnHost, instance.IdnHost, StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != instance.Port || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) return false;
        var path = Uri.UnescapeDataString(endpoint.AbsolutePath).TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        var expected = Uri.UnescapeDataString(instance.AbsolutePath).TrimEnd('/') + "/" + snapshot.PathWithNamespace;
        return string.Equals(path, expected, StringComparison.Ordinal);
    }
}
