using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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

    public async Task LinkChangeRequestAsync(Guid changeRequestId, IEnumerable<Guid>? problemReportIds,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        var selected = Selected(problemReportIds);
        await AddLinksAsync("ChangeRequest", changeRequestId, "ProposedCorrectiveAction", selected, actor, now, ct);
        await StartImplementationForOpenReportsAsync(selected, actor, now, ct);
    }

    public async Task ReplaceDraftChangeRequestLinksAsync(SystemChangeRequest request,
        IEnumerable<Guid>? problemReportIds, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (request.State != ScrState.Draft)
            throw new DomainException("Problem Report links can be changed only while the change request is a Draft.");
        var selected = Selected(problemReportIds);
        var validation = await ValidateSelectionAsync(request.ProjectId, request.TargetReleaseId, selected, ct);
        if (validation is not null) throw new DomainException(validation);

        var existing = await db.ProblemReportLinks.Where(link => link.ArtifactType == "ChangeRequest"
            && link.ArtifactId == request.Id && link.Relationship == "ProposedCorrectiveAction").ToListAsync(ct);
        var existingIds = existing.Select(link => link.ProblemReportId).ToHashSet();
        var selectedIds = selected.ToHashSet();
        if (existingIds.SetEquals(selectedIds)) return;

        db.ProblemReportLinks.RemoveRange(existing.Where(link => !selectedIds.Contains(link.ProblemReportId)));
        await AddLinksAsync("ChangeRequest", request.Id, "ProposedCorrectiveAction",
            selectedIds.Except(existingIds), actor, now, ct);
        await StartImplementationForOpenReportsAsync(selectedIds.Except(existingIds), actor, now, ct);
        db.AuditEvents.Add(new AuditEvent(request.Id, "ProblemReportLinksUpdated", actor,
            $"Updated the driving Problem Report set to {selectedIds.Count} record(s).", now));
    }

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

    private async Task StartImplementationForOpenReportsAsync(IEnumerable<Guid> reportIds, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var ids = reportIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var reports = await db.ProblemReports.Where(report => ids.Contains(report.Id) && report.State == ProblemReportState.Open).ToListAsync(ct);
        foreach (var report in reports)
        {
            report.BeginImplementation(actor, now, automatic: true);
            var snapshot = JsonSerializer.Serialize(new { report.Id, report.ProjectId, report.ReportNumber, report.Revision, report.DisplayNumber, report.Title, report.ResponsibleEngineerId, report.TargetReleaseId, state = report.State.ToString(), report.Version });
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision, "ImplementationStartedByLinkedChangeRequest", actor, report.CanonicalHash(), snapshot, now));
        }
    }

    private static List<Guid> Selected(IEnumerable<Guid>? ids) =>
        (ids ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
}
