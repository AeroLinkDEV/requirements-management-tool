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
                    && link.Relationship == ProblemReportRelationshipPolicy.BuildScope))
            .Select(report => report.Id).ToListAsync(ct);
        return valid.Count == selected.Count
            ? null
            : "Every selected PR must belong to this Project and target build.";
    }

    public async Task LinkChangeRequestAsync(Guid changeRequestId, string changeRequestDisplayNumber,
        IEnumerable<Guid>? problemReportIds,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        var selected = Selected(problemReportIds);
        await AddLinksAsync("ChangeRequest", changeRequestId, ProblemReportRelationshipPolicy.ProposedCorrectiveAction,
            ProblemReportRelationshipProducer.ChangeRequestWorkflow, selected, actor, now, ct);
        await StartImplementationForOpenReportsAsync(changeRequestId, changeRequestDisplayNumber,
            selected, actor, now, ct);
    }

    public async Task ReplaceDraftChangeRequestLinksAsync(SystemChangeRequest request,
        IEnumerable<Guid>? problemReportIds, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (request.State != ChangeRequestState.Draft)
            throw new DomainException("Problem Report links can be changed only while the change request is a Draft.");
        var selected = Selected(problemReportIds);
        var validation = await ValidateSelectionAsync(request.ProjectId, request.TargetReleaseId, selected, ct);
        if (validation is not null) throw new DomainException(validation);

        var existing = await db.ProblemReportLinks.Where(link => link.ArtifactType == "ChangeRequest"
            && link.ArtifactId == request.Id && link.Relationship == ProblemReportRelationshipPolicy.ProposedCorrectiveAction).ToListAsync(ct);
        var existingIds = existing.Select(link => link.ProblemReportId).ToHashSet();
        var selectedIds = selected.ToHashSet();
        if (existingIds.SetEquals(selectedIds)) return;

        var removedIds = existingIds.Except(selectedIds).ToList();
        foreach (var removedReportId in removedIds)
            await InvalidateForControlledLinkChangeAsync(removedReportId, actor,
                "ProposedCorrectiveActionRemoved", now, ct);
        db.ProblemReportLinks.RemoveRange(existing.Where(link => !selectedIds.Contains(link.ProblemReportId)));
        await AddLinksAsync("ChangeRequest", request.Id, ProblemReportRelationshipPolicy.ProposedCorrectiveAction,
            ProblemReportRelationshipProducer.ChangeRequestWorkflow, selectedIds.Except(existingIds), actor, now, ct);
        await StartImplementationForOpenReportsAsync(request.Id, request.DisplayNumber,
            selectedIds.Except(existingIds), actor, now, ct);
        await ReconcileRemovedAutomaticImplementationAsync(request, removedIds, actor, now, ct);
        db.AuditEvents.Add(new AuditEvent(request.Id, "ProblemReportLinksUpdated", actor,
            $"Updated the driving Problem Report set to {selectedIds.Count} record(s).", now));
    }

    public Task LinkTestChangeRequestAsync(Guid testChangeRequestId, IEnumerable<Guid>? problemReportIds,
        string actor, DateTimeOffset now, CancellationToken ct)
        => AddLinksAsync("TestChangeRequest", testChangeRequestId, ProblemReportRelationshipPolicy.VerificationForProblem,
            ProblemReportRelationshipProducer.TestChangeRequestWorkflow, problemReportIds, actor, now, ct);

    public async Task PropagateToTestChangeRequestAsync(Guid changeRequestId, Guid testChangeRequestId,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        var reportIds = await db.ProblemReportLinks.AsNoTracking()
            .Where(link => link.ArtifactType == "ChangeRequest" && link.ArtifactId == changeRequestId
                && link.Relationship == ProblemReportRelationshipPolicy.ProposedCorrectiveAction)
            .Select(link => link.ProblemReportId).Distinct().ToListAsync(ct);
        await LinkTestChangeRequestAsync(testChangeRequestId, reportIds, actor, now, ct);
    }

    public async Task RecordApprovedCorrectiveActionsAsync(SystemChangeRequest request, string actor,
        DateTimeOffset now, CancellationToken ct)
    {
        if (request.State is not (ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline)) return;
        var reportIds = await db.ProblemReportLinks.AsNoTracking()
            .Where(link => link.ArtifactType == "ChangeRequest" && link.ArtifactId == request.Id
                && link.Relationship == ProblemReportRelationshipPolicy.ProposedCorrectiveAction)
            .Select(link => link.ProblemReportId).Distinct().ToListAsync(ct);
        await AddLinksAsync("ChangeRequest", request.Id, ProblemReportRelationshipPolicy.ApprovedCorrectiveAction,
            ProblemReportRelationshipProducer.ChangeRequestWorkflow, reportIds, actor, now, ct);
    }

    private async Task AddLinksAsync(string artifactType, Guid artifactId, string relationship,
        ProblemReportRelationshipProducer producer,
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
            {
                await InvalidateForControlledLinkChangeAsync(reportId, actor,
                    $"{relationship}Linked", now, ct);
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(reportId, artifactType,
                    artifactId, relationship, producer, actor, now));
            }
        }
    }

    private async Task InvalidateForControlledLinkChangeAsync(Guid reportId, string actor,
        string operation, DateTimeOffset now, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<ProblemReport>()
            .Select(entry => entry.Entity).SingleOrDefault(report => report.Id == reportId);
        var report = tracked ?? await db.ProblemReports.SingleOrDefaultAsync(item => item.Id == reportId, ct);
        if (report is null) throw new DomainException("The selected Problem Report does not exist.");
        if (report.PrepareControlledRelationshipChange(actor, now))
            await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(report, actor,
                operation, now, ct);
    }

    private async Task StartImplementationForOpenReportsAsync(Guid changeRequestId, string changeRequestDisplayNumber,
        IEnumerable<Guid> reportIds, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var ids = reportIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var reports = await db.ProblemReports.Where(report => ids.Contains(report.Id) && report.State == ProblemReportState.Open).ToListAsync(ct);
        foreach (var report in reports.Where(report => report.State == ProblemReportState.Open))
        {
            report.BeginImplementation(actor, now, automatic: true);
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
                "ImplementationStartedByLinkedChangeRequest", actor, report.CanonicalHash(),
                report.CanonicalSnapshot(), now, detail:
                $"Automatically entered Implementing when Draft {changeRequestDisplayNumber} ({changeRequestId}) was linked as a proposed corrective action.",
                evidenceJson: JsonSerializer.Serialize(new
                {
                    policy = "DraftCorrectiveActionImplementationV1",
                    changeRequestId,
                    changeRequestDisplayNumber,
                    reason = "The Draft change request became a proposed corrective action for this Open Problem Report.",
                })));
        }
    }

    private async Task ReconcileRemovedAutomaticImplementationAsync(SystemChangeRequest request,
        IReadOnlyCollection<Guid> removedReportIds, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (removedReportIds.Count == 0) return;
        var reports = await db.ProblemReports.Where(report => removedReportIds.Contains(report.Id)
            && report.State == ProblemReportState.Implementing).ToListAsync(ct);
        foreach (var report in reports)
        {
            if (await HasAnotherImplementationSourceAsync(report.Id, request.Id, ct)) continue;

            var revisions = await db.ProblemReportRevisions.AsNoTracking()
                .Where(item => item.ProblemReportId == report.Id).ToListAsync(ct);
            var automaticStart = revisions.OrderBy(item => item.OccurredAt)
                .LastOrDefault(item => item.EventType is "ImplementationStartedByLinkedChangeRequest"
                    or "ImplementationStarted" or "InvestigationRecorded"
                    or "ImplementationRevertedAfterDraftCorrectiveActionRemoved");
            if (automaticStart?.EventType != "ImplementationStartedByLinkedChangeRequest"
                || automaticStart.SnapshotSchemaVersion < ProblemReportEvidenceContract.SchemaVersion)
                continue;

            ProblemReportEvidenceSnapshot? startSnapshot;
            try
            {
                startSnapshot = JsonSerializer.Deserialize<ProblemReportEvidenceSnapshot>(automaticStart.SnapshotJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                continue;
            }
            if (startSnapshot is null || HasSubstantiveImplementationAfter(startSnapshot, report)) continue;

            if (!HasAutomaticRoutingEvidence(automaticStart.EvidenceJson)) continue;

            report.RevertAutomaticImplementation(actor, now);
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
                "ImplementationRevertedAfterDraftCorrectiveActionRemoved", actor, report.CanonicalHash(),
                report.CanonicalSnapshot(), now, detail:
                $"Returned to Open after Draft {request.DisplayNumber} ({request.Id}) was removed; it was the sole automatic implementation source and no implementation work had been recorded.",
                evidenceJson: JsonSerializer.Serialize(new
                {
                    policy = "DraftCorrectiveActionImplementationV1",
                    removedChangeRequestId = request.Id,
                    removedChangeRequestDisplayNumber = request.DisplayNumber,
                    reason = "The removed Draft corrective action was the sole automatic implementation source and no substantive implementation work followed it.",
                })));
        }
    }

    private async Task<bool> HasAnotherImplementationSourceAsync(Guid reportId, Guid removedChangeRequestId,
        CancellationToken ct)
    {
        var persisted = await db.ProblemReportLinks.AsNoTracking().AnyAsync(link => link.ProblemReportId == reportId
            && link.ArtifactType == "ChangeRequest" && link.ArtifactId != removedChangeRequestId
            && (link.Relationship == ProblemReportRelationshipPolicy.ProposedCorrectiveAction
                || link.Relationship == ProblemReportRelationshipPolicy.ApprovedCorrectiveAction), ct);
        if (persisted) return true;
        return db.ChangeTracker.Entries<ProblemReportLink>().Any(entry => entry.State != EntityState.Deleted
            && entry.Entity.ProblemReportId == reportId && entry.Entity.ArtifactType == "ChangeRequest"
            && entry.Entity.ArtifactId != removedChangeRequestId
            && (entry.Entity.Relationship == ProblemReportRelationshipPolicy.ProposedCorrectiveAction
                || entry.Entity.Relationship == ProblemReportRelationshipPolicy.ApprovedCorrectiveAction));
    }

    private static bool HasSubstantiveImplementationAfter(ProblemReportEvidenceSnapshot start, ProblemReport current) =>
        !string.Equals(start.Analysis, current.Analysis, StringComparison.Ordinal)
        || !string.Equals(start.RootCause, current.RootCause, StringComparison.Ordinal)
        || !string.Equals(start.Effects, current.Effects, StringComparison.Ordinal)
        || !string.Equals(start.Containment, current.Containment, StringComparison.Ordinal)
        || !string.Equals(start.CorrectiveAction, current.CorrectiveAction, StringComparison.Ordinal);

    private static bool HasAutomaticRoutingEvidence(string? evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(evidenceJson);
            return document.RootElement.TryGetProperty("policy", out var value)
                && value.GetString() == "DraftCorrectiveActionImplementationV1"
                && document.RootElement.TryGetProperty("changeRequestId", out var requestId)
                && requestId.TryGetGuid(out var referenced) && referenced != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<Guid> Selected(IEnumerable<Guid>? ids) =>
        (ids ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
}
