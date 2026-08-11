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
    public const int SchemaVersion = 1;

    public async Task<ProblemReportClosureCandidate> CreateAsync(ProblemReport report,
        TestExecution execution, ProblemReportLink resolutionLink, string actor, DateTimeOffset now,
        CancellationToken ct)
    {
        if (report.State != ProblemReportState.AwaitingSqaClosure
            || report.ResolutionVerificationExecutionId != execution.Id)
            throw new InvalidOperationException("The Problem Report must first accept this verification execution.");
        if (await db.ProblemReportClosureCandidates.AnyAsync(item => item.ProblemReportId == report.Id
                && item.State == ProblemReportClosureCandidateState.Pending, ct))
            throw new InvalidOperationException("This Problem Report already has a pending closure candidate.");

        var sequences = await db.ProblemReportClosureCandidates.AsNoTracking()
            .Where(item => item.ProblemReportId == report.Id && item.ReportRevision == report.Revision)
            .Select(item => item.Sequence).ToListAsync(ct);
        var reportJson = ReportSnapshot(report);
        var evidenceJson = VerificationEvidence(execution);
        var linksJson = await LinksManifestAsync(report.Id, resolutionLink, ct);
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
            manifestHash, actor, now);
        db.ProblemReportClosureCandidates.Add(candidate);
        return candidate;
    }

    public async Task<ProblemReportClosureCandidate?> InvalidatePendingAsync(ProblemReport report,
        string actor, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var candidate = await db.ProblemReportClosureCandidates.SingleOrDefaultAsync(item =>
            item.ProblemReportId == report.Id && item.State == ProblemReportClosureCandidateState.Pending, ct);
        if (candidate is null) return null;
        candidate.Invalidate(actor, reason, now);
        db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
            "ClosureVerificationInvalidatedByChange", actor, report.CanonicalHash(),
            ProblemReportControlledEditingAdapter.EvidenceSnapshot(report), now));
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
            || report.State != ProblemReportState.AwaitingSqaClosure
            || report.ResolutionVerificationExecutionId != candidate.VerificationExecutionId)
            return ProblemReportClosureCandidateDecision.Reject("pr_closure_candidate_stale",
                "The SQA closure candidate was invalidated by a later change. Record new verification before closure.", candidate);
        if (candidate.ReportVersion != report.Version
            || !string.Equals(candidate.ReportSnapshotHash, Hash(ReportSnapshot(report)), StringComparison.Ordinal))
            return ProblemReportClosureCandidateDecision.Reject("pr_closure_candidate_stale",
                "The Problem Report changed after its closure candidate was selected. Record new verification before closure.", candidate);

        var linksJson = await LinksManifestAsync(report.Id, additionalLink: null, ct);
        if (!string.Equals(candidate.LinksManifestHash, Hash(linksJson), StringComparison.Ordinal))
            return ProblemReportClosureCandidateDecision.Reject("pr_closure_candidate_stale",
                "The Problem Report evidence relationships changed after verification. Record new verification before closure.", candidate);

        var execution = await db.TestExecutions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == candidate.VerificationExecutionId, ct);
        if (execution is null
            || !string.Equals(candidate.VerificationEvidenceHash, Hash(VerificationEvidence(execution)), StringComparison.Ordinal))
            return ProblemReportClosureCandidateDecision.Reject("pr_closure_candidate_stale",
                "The selected verification evidence no longer matches the SQA closure candidate.", candidate);
        return ProblemReportClosureCandidateDecision.Accept(candidate);
    }

    public static string ReportSnapshot(ProblemReport report) => JsonSerializer.Serialize(new
    {
        contract = "aerolink.problem-report-closure-review",
        schemaVersion = SchemaVersion,
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
        type = report.Type.ToString(),
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
        report.WaiverRationale,
        report.WaivedBy,
        report.WaivedAt,
        state = report.State.ToString(),
        report.CreatedAt,
        report.UpdatedAt,
        report.Version,
    });

    private async Task<string> LinksManifestAsync(Guid reportId, ProblemReportLink? additionalLink,
        CancellationToken ct)
    {
        var links = await db.ProblemReportLinks.AsNoTracking()
            .Where(item => item.ProblemReportId == reportId).ToListAsync(ct);
        if (additionalLink is not null && links.All(item => item.Id != additionalLink.Id)) links.Add(additionalLink);
        return JsonSerializer.Serialize(new
        {
            contract = "aerolink.problem-report-closure-links",
            schemaVersion = SchemaVersion,
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

    private static string VerificationEvidence(TestExecution execution) => JsonSerializer.Serialize(new
    {
        contract = "aerolink.problem-report-closure-verification",
        schemaVersion = SchemaVersion,
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

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
