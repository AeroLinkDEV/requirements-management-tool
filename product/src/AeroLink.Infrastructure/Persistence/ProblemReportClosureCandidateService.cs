using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ProblemReportClosureCandidateDecision(
    bool Accepted, string? Code, string? Error, ProblemReportClosureCandidate? Candidate)
{
    public static ProblemReportClosureCandidateDecision Accept(ProblemReportClosureCandidate candidate) =>
        new(true, null, null, candidate);
    public static ProblemReportClosureCandidateDecision Reject(string code, string error,
        ProblemReportClosureCandidate? candidate = null) => new(false, code, error, candidate);
}

/// <summary>
/// Creates and validates the exact Problem Report configuration placed before independent SQA. The JSON
/// contracts are deliberately versioned and deterministic so #451 can render the approved candidate rather
/// than inventing a second closure-snapshot mechanism.
/// </summary>
public sealed class ProblemReportClosureCandidateService(AeroLinkDbContext db)
{
    public const int SchemaVersion = 2;
    public const int ClosurePackageSchemaVersion = 4;

    public async Task<ProblemReportClosureCandidate> CreateAsync(ProblemReport report,
        TestExecution execution, ProblemReportLink resolutionLink, string actor, DateTimeOffset now,
        CancellationToken ct)
    {
        if (report.State != ProblemReportState.WaitingForSqaToClose
            || report.ResolutionVerificationExecutionId != execution.Id)
            throw new InvalidOperationException("The Problem Report must first accept this verification execution.");
        if (await db.ProblemReportClosureCandidates.AnyAsync(item => item.ProblemReportId == report.Id
                && item.State == ProblemReportClosureCandidateState.Pending, ct))
            throw new InvalidOperationException("This Problem Report already has a pending closure candidate.");

        var sequences = await db.ProblemReportClosureCandidates.AsNoTracking()
            .Where(item => item.ProblemReportId == report.Id && item.ReportRevision == report.Revision)
            .Select(item => item.Sequence).ToListAsync(ct);
        var reportJson = ReportSnapshot(report);
        var evidenceJson = VerificationEvidence(execution, SchemaVersion);
        var linksJson = await LinksManifestAsync(report.Id, resolutionLink, SchemaVersion, ct);
        var reportHash = Hash(reportJson);
        var evidenceHash = Hash(evidenceJson);
        var linksHash = Hash(linksJson);
        var manifestHash = Hash(JsonSerializer.Serialize(new
        {
            contract = "aerolink.problem-report-closure-candidate-manifest",
            schemaVersion = SchemaVersion,
            problemReportId = report.Id,
            reportRevision = report.Revision,
            reportVersion = report.Version,
            reportSnapshotSchemaVersion = ProblemReportEvidenceContract.SchemaVersion,
            reportHash,
            verificationExecutionId = execution.Id,
            evidenceHash,
            linksHash,
            selectedBy = actor,
            selectedAt = now,
        }));
        var candidate = new ProblemReportClosureCandidate(report.Id, report.Revision,
            sequences.DefaultIfEmpty(0).Max() + 1, SchemaVersion, report.Version,
            reportJson, reportHash, execution.Id, evidenceJson, evidenceHash, linksJson, linksHash,
            manifestHash, actor, now, ProblemReportEvidenceContract.SchemaVersion);
        db.ProblemReportClosureCandidates.Add(candidate);
        return candidate;
    }

    public async Task<ProblemReportClosureCandidate?> InvalidatePendingAsync(ProblemReport report,
        string actor, string reason, DateTimeOffset now, CancellationToken ct,
        ProblemReportState? fromState = null, ProblemReportState? toState = null, string? rationale = null)
    {
        var candidate = await db.ProblemReportClosureCandidates.SingleOrDefaultAsync(item =>
            item.ProblemReportId == report.Id && item.State == ProblemReportClosureCandidateState.Pending, ct);
        if (candidate is null) return null;
        candidate.Invalidate(actor, reason, now);
        var source = ProblemReportTransitionPolicy.Canonical(fromState ?? report.State);
        var target = ProblemReportTransitionPolicy.Canonical(toState ?? report.State);
        var transitionRationale = rationale?.Trim();
        if (source != target && string.IsNullOrWhiteSpace(transitionRationale))
            transitionRationale = reason;
        db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
            "ClosureVerificationInvalidatedByChange", actor, report.CanonicalHash(),
            ProblemReportControlledEditingAdapter.EvidenceSnapshot(report), now,
            detail: reason, fromState: source.ToString(), toState: target.ToString(), rationale: transitionRationale));
        return candidate;
    }

    public async Task<ProblemReportClosureCandidateDecision> ValidateForApprovalAsync(
        ProblemReport report, CancellationToken ct)
    {
        var candidates = await db.ProblemReportClosureCandidates
            .Where(item => item.ProblemReportId == report.Id && item.ReportRevision == report.Revision)
            .ToListAsync(ct);
        var candidate = candidates.OrderByDescending(item => item.Sequence).FirstOrDefault();
        if (candidate is null)
            return ProblemReportClosureCandidateDecision.Reject("pr_closure_candidate_missing",
                "Select valid closure verification to create an exact SQA closure candidate.");
        if (candidate.State != ProblemReportClosureCandidateState.Pending
            || report.State != ProblemReportState.WaitingForSqaToClose
            || report.ResolutionVerificationExecutionId != candidate.VerificationExecutionId)
            return ProblemReportClosureCandidateDecision.Reject("pr_closure_candidate_stale",
                "The SQA closure candidate was invalidated by a later change. Record new verification before closure.", candidate);
        if (candidate.ReportVersion != report.Version
            || !string.Equals(candidate.ReportSnapshotHash,
                Hash(ReportSnapshotForSchema(report, candidate.ReportSnapshotSchemaVersion)), StringComparison.Ordinal))
            return ProblemReportClosureCandidateDecision.Reject("pr_closure_candidate_stale",
                "The Problem Report changed after its closure candidate was selected. Record new verification before closure.", candidate);

        var linksJson = await LinksManifestAsync(report.Id, additionalLink: null, candidate.SchemaVersion, ct);
        if (!string.Equals(candidate.LinksManifestHash, Hash(linksJson), StringComparison.Ordinal))
            return ProblemReportClosureCandidateDecision.Reject("pr_closure_candidate_stale",
                "The Problem Report evidence relationships changed after verification. Record new verification before closure.", candidate);

        var execution = await db.TestExecutions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == candidate.VerificationExecutionId, ct);
        if (execution is null
            || !string.Equals(candidate.VerificationEvidenceHash,
                Hash(VerificationEvidence(execution, candidate.SchemaVersion)), StringComparison.Ordinal))
            return ProblemReportClosureCandidateDecision.Reject("pr_closure_candidate_stale",
                "The selected verification evidence no longer matches the SQA closure candidate.", candidate);
        return ProblemReportClosureCandidateDecision.Accept(candidate);
    }

    public async Task FreezeForApprovalAsync(ProblemReport report, ProblemReportClosureCandidate candidate,
        ProblemReportRevision closureRevision, string actor, Guid actorAccountId, string approvalAuthority,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (report.State != ProblemReportState.Closed || report.ClosureApprovedAt != now
            || !string.Equals(report.ClosureApprovedByName, actor, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Problem Report must be closed by this approval before its package is frozen.");

        var revisions = await db.ProblemReportRevisions.AsNoTracking()
            .Where(item => item.ProblemReportId == report.Id).ToListAsync(ct);
        if (revisions.All(item => item.Id != closureRevision.Id)) revisions.Add(closureRevision);
        var releaseWaivers = await db.ReadinessWaivers.AsNoTracking().Where(item => item.ProjectId == report.ProjectId
            && item.BlockerType == "ProblemReportReleaseBlocker" && item.BlockerId == report.Id).ToListAsync(ct);
        var activeReleaseWaiver = releaseWaivers.FirstOrDefault(item => item.IsActiveFor(report, now));
        var finalReportJson = ReportSnapshot(report);
        var packageJson = JsonSerializer.Serialize(new
        {
            contract = "aerolink.problem-report-closure-package",
            schemaVersion = ClosurePackageSchemaVersion,
            candidateSchemaVersion = candidate.SchemaVersion,
            reportSnapshotSchemaVersion = candidate.ReportSnapshotSchemaVersion,
            provenance = "FrozenAtApproval",
            candidate = new
            {
                id = candidate.Id,
                problemReportId = candidate.ProblemReportId,
                reportRevision = candidate.ReportRevision,
                sequence = candidate.Sequence,
                reportVersion = candidate.ReportVersion,
                reportSnapshotSchemaVersion = candidate.ReportSnapshotSchemaVersion,
                manifestHash = candidate.ManifestHash,
                selectedBy = candidate.SelectedBy,
                selectedAt = candidate.SelectedAt,
                reportSnapshotJson = candidate.ReportSnapshotJson,
                reportSnapshotHash = candidate.ReportSnapshotHash,
            },
            closure = new
            {
                problemReportId = report.Id,
                displayNumber = report.DisplayNumber,
                revision = report.Revision,
                version = report.Version,
                reportSnapshotSchemaVersion = ProblemReportEvidenceContract.SchemaVersion,
                reportSnapshotJson = finalReportJson,
                reportSnapshotHash = Hash(finalReportJson),
                approvedByAccountId = actorAccountId,
                approvedBy = actor,
                approvedAt = now,
                authority = approvalAuthority,
                authorityMeaning = "IndependentSqaClosure",
                approvalRevisionId = closureRevision.Id,
            },
            verification = new
            {
                verificationExecutionId = candidate.VerificationExecutionId,
                verificationEvidenceJson = candidate.VerificationEvidenceJson,
                verificationEvidenceHash = candidate.VerificationEvidenceHash,
            },
            relationships = new
            {
                linksManifestJson = candidate.LinksManifestJson,
                linksManifestHash = candidate.LinksManifestHash,
            },
            releaseWaiver = activeReleaseWaiver is null ? null : new
            {
                activeReleaseWaiver.Id,
                activeReleaseWaiver.BlockerRevision,
                activeReleaseWaiver.BlockerVersion,
                activeReleaseWaiver.Rationale,
                activeReleaseWaiver.ApprovedByAccountId,
                activeReleaseWaiver.ApprovedBy,
                activeReleaseWaiver.ApprovalAuthority,
                activeReleaseWaiver.SignatureMeaning,
                activeReleaseWaiver.CreatedAt,
                activeReleaseWaiver.ExpiresAt,
            },
            history = revisions.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id).Select(item => new
            {
                id = item.Id,
                revision = item.Revision,
                eventType = item.EventType,
                actor = item.Actor,
                snapshotSchemaVersion = item.SnapshotSchemaVersion,
                snapshotHash = item.SnapshotHash,
                snapshotJson = item.SnapshotJson,
                occurredAt = item.OccurredAt,
            }),
        });
        candidate.Approve(actor, actorAccountId, now, packageJson, Hash(packageJson));
    }

    public static string ReportSnapshot(ProblemReport report) => ProblemReportEvidenceContract.Serialize(report);

    private static string ReportSnapshotForSchema(ProblemReport report, int schemaVersion) => schemaVersion switch
    {
        ProblemReportEvidenceContract.SchemaVersion => ReportSnapshot(report),
        1 => LegacyV1ReportSnapshot(report),
        _ => throw new InvalidOperationException($"Problem Report snapshot schema {schemaVersion} is not supported."),
    };

    // Reader/validator only. Existing v1 candidates keep the exact contract they were selected under; no new
    // evidence is written through this independently maintained legacy projection.
    private static string LegacyV1ReportSnapshot(ProblemReport report) => JsonSerializer.Serialize(new
    {
        contract = "aerolink.problem-report-closure-review",
        schemaVersion = 1,
        report.Id,
        report.ProjectId,
        report.ReportNumber,
        report.Revision,
        report.DisplayNumber,
        report.Title,
        report.Problem,
        report.ProblemRich,
        report.AdditionalInformation,
        report.AdditionalInformationRich,
        report.Analysis,
        report.ReportedBy,
        report.ResponsibleEngineerId,
        report.TargetReleaseId,
        category = report.Category?.ToString(),
        categoryProvenance = report.CategoryProvenance?.ToString(),
        report.Workaround,
        report.Classification,
        severity = report.Severity.ToString(),
        priority = report.Priority.ToString(),
        report.Origin,
        report.AffectedConfiguration,
        report.RootCause,
        report.Effects,
        report.Containment,
        report.CorrectiveAction,
        report.SystemAircraftImpact,
        report.ImpactAssessmentJson,
        disposition = report.Disposition?.ToString(),
        report.DispositionRationale,
        report.ResolutionVerificationExecutionId,
        report.IsReleaseBlocker,
        report.ReleaseBlockerVersion,
        report.WaiverRationale,
        report.WaivedBy,
        report.WaivedAt,
        state = report.State.ToString(),
        report.CreatedAt,
        report.UpdatedAt,
        report.Version,
    });

    private async Task<string> LinksManifestAsync(Guid reportId, ProblemReportLink? additionalLink,
        int schemaVersion, CancellationToken ct)
    {
        var links = await db.ProblemReportLinks.AsNoTracking()
            .Where(item => item.ProblemReportId == reportId).ToListAsync(ct);
        if (additionalLink is not null && links.All(item => item.Id != additionalLink.Id)) links.Add(additionalLink);
        return JsonSerializer.Serialize(new
        {
            contract = "aerolink.problem-report-closure-links",
            schemaVersion,
            problemReportId = reportId,
            links = links.OrderBy(item => item.Relationship, StringComparer.Ordinal)
                .ThenBy(item => item.ArtifactType, StringComparer.Ordinal)
                .ThenBy(item => item.ArtifactId)
                .ThenBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.ArtifactType,
                    item.ArtifactId,
                    item.Relationship,
                    item.AddedBy,
                    item.AddedAt,
                }),
        });
    }

    private static string VerificationEvidence(TestExecution execution, int schemaVersion) => JsonSerializer.Serialize(new
    {
        contract = "aerolink.problem-report-closure-verification",
        schemaVersion,
        execution.Id,
        execution.ProjectId,
        execution.ReleaseId,
        execution.ProcedureRevisionId,
        execution.SoftwareBuildId,
        execution.RetestOfExecutionId,
        outcome = execution.Outcome.ToString(),
        execution.ExecutedBy,
        execution.Configuration,
        execution.Determination,
        execution.EvidenceReference,
        execution.ExecutedAt,
        execution.RecordedAt,
    });

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
