using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Operator-bound display classification, independent of engineering or write authority.</summary>
public sealed record GitLabSyntheticDemonstration(Guid ConfigurationId, long ConfigurationVersion, long RemoteProjectId)
{
    public static async Task<GitLabSyntheticDemonstration?> ReadAsync(AeroLinkDbContext db, Guid projectId,
        ProjectGitLabOptions settings, CancellationToken ct)
    {
        if (!Guid.TryParse(settings.SyntheticDemoProjectId, out var configuredProject)
            || configuredProject == Guid.Empty || configuredProject != projectId
            || !long.TryParse(settings.SyntheticDemoRemoteProjectId, NumberStyles.None, CultureInfo.InvariantCulture,
                out var remoteProject) || remoteProject <= 0)
            return null;

        var isShowcase = await (from project in db.Projects.AsNoTracking()
                               join program in db.Programs.AsNoTracking() on project.ProgramId equals program.Id
                               where project.Id == projectId && program.Code == FmsShowcaseSeeder.ProgramCode
                               select project.Id).AnyAsync(ct);
        if (!isShowcase) return null;
        var repository = await db.ProjectRepositoryConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (repository is null || repository.RemoteProjectId != remoteProject
            || !GitLabMetadataReader.MatchesVerifiedRepositoryIdentity(repository, settings.BaseUrl))
            return null;
        return new(repository.Id, repository.Version, remoteProject);
    }
}
