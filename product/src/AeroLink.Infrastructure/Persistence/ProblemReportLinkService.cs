using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Keeps problem-report relationships build scoped and records the point at which a proposed correction
/// becomes an approved corrective action. Git or requirements remain authoritative for their own records;
/// these links are the controlled thread between them.
/// </summary>
public sealed class ProblemReportLinkService(AeroLinkDbContext db)
{
    public async Task<string?> ValidateSelectionAsync(Guid projectId, Guid releaseId,
        IEnumerable<Guid>? problemReportIds, CancellationToken ct)
    {
        var selected = Selected(problemReportIds);
        if (selected.Count == 0) return null;
        var valid = await db.ProblemReports.AsNoTracking()
            .Where(report => selected.Contains(report.Id) && report.ProjectId == projectId
                && db.ProblemReportLinks.Any(link => link.ProblemReportId == report.Id
                    && link.ArtifactType == "Release" && link.ArtifactId == releaseId
                    && link.Relationship == "BuildScope"))
            .Select(report => report.Id).ToListAsync(ct);
        return valid.Count == selected.Count
            ? null
            : "Every selected PR must belong to this Project and target build.";
    }

    public Task LinkChangeRequestAsync(Guid changeRequestId, IEnumerable<Guid>? problemReportIds,
        string actor, DateTimeOffset now, CancellationToken ct)
        => AddLinksAsync("ChangeRequest", changeRequestId, "ProposedCorrectiveAction", problemReportIds, actor, now, ct);

    public Task LinkTestChangeRequestAsync(Guid testChangeRequestId, IEnumerable<Guid>? problemReportIds,
        string actor, DateTimeOffset now, CancellationToken ct)
        => AddLinksAsync("TestChangeRequest", testChangeRequestId, "VerificationForProblem", problemReportIds, actor, now, ct);

    public async Task PropagateToTestChangeRequestAsync(Guid changeRequestId, Guid testChangeRequestId,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        var reportIds = await db.ProblemReportLinks.AsNoTracking()
            .Where(link => link.ArtifactType == "ChangeRequest" && link.ArtifactId == changeRequestId
                && link.Relationship == "ProposedCorrectiveAction")
            .Select(link => link.ProblemReportId).Distinct().ToListAsync(ct);
        await LinkTestChangeRequestAsync(testChangeRequestId, reportIds, actor, now, ct);
    }

    public async Task RecordApprovedCorrectiveActionsAsync(SystemChangeRequest request, string actor,
        DateTimeOffset now, CancellationToken ct)
    {
        if (request.State is not (ScrState.Approved or ScrState.SelectedForBaseline)) return;
        var reportIds = await db.ProblemReportLinks.AsNoTracking()
            .Where(link => link.ArtifactType == "ChangeRequest" && link.ArtifactId == request.Id
                && link.Relationship == "ProposedCorrectiveAction")
            .Select(link => link.ProblemReportId).Distinct().ToListAsync(ct);
        await AddLinksAsync("ChangeRequest", request.Id, "ApprovedCorrectiveAction", reportIds, actor, now, ct);
    }

    private async Task AddLinksAsync(string artifactType, Guid artifactId, string relationship,
        IEnumerable<Guid>? problemReportIds, string actor, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var reportId in Selected(problemReportIds))
        {
            var alreadyTracked = db.ChangeTracker.Entries<ProblemReportLink>().Any(entry =>
                entry.Entity.ProblemReportId == reportId && entry.Entity.ArtifactType == artifactType
                && entry.Entity.ArtifactId == artifactId && entry.Entity.Relationship == relationship);
            var alreadySaved = !alreadyTracked && await db.ProblemReportLinks.AsNoTracking().AnyAsync(link =>
                link.ProblemReportId == reportId && link.ArtifactType == artifactType
                && link.ArtifactId == artifactId && link.Relationship == relationship, ct);
            if (!alreadyTracked && !alreadySaved)
                db.ProblemReportLinks.Add(new ProblemReportLink(reportId, artifactType, artifactId,
                    relationship, actor, now));
        }
    }

    private static List<Guid> Selected(IEnumerable<Guid>? ids) =>
        (ids ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
}
