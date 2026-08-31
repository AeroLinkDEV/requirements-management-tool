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

    private static readonly JsonSerializerOptions HistoricalSnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

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
        var reportSnapshot = await CurrentReportSnapshotAsync(report, ct);
        var reportJson = reportSnapshot.Json;
        var evidenceJson = VerificationEvidence(execution, SchemaVersion);
        var linksJson = await LinksManifestAsync(report.Id, resolutionLink, SchemaVersion, ct);
        var reportHash = reportSnapshot.Hash;
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
        ProblemReportState? fromState = null, ProblemReportState? toState = null, string? rationale = null,
        string? actorDisplayName = null)
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
        // Callers that hold the authenticated session pass the name straight in. The rest — the controlled
        // check-in engine and the link service, both of which carry the actor as a bare handle several layers
        // up — get it read here instead, once, at the moment the event is written.
        //
        // Reading it here is still capture, not live resolution: the value is frozen onto the immutable row
        // and never consulted again, so it means the same thing as the session-supplied one. What it must not
        // become is a lookup on the *read* path, which is what would let a later rename rewrite this event.
        var capturedName = actorDisplayName;
        if (string.IsNullOrWhiteSpace(capturedName))
        {
            var handle = actor.Trim().ToLowerInvariant();
            capturedName = await db.UserAccounts.AsNoTracking()
                .Where(account => account.UserName == handle)
                .Select(account => account.DisplayName)
                .SingleOrDefaultAsync(ct);
        }
        var evidence = await ProblemReportAttachmentEvidence.SnapshotAsync(db, report, ct);
        db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
            "ClosureVerificationInvalidatedByChange", actor, evidence.Hash,
            evidence.Json, now,
            detail: reason, fromState: source.ToString(), toState: target.ToString(), rationale: transitionRationale,
            actorDisplayName: capturedName));
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
        var currentReportSnapshot = await ReportSnapshotForSchemaAsync(report, candidate.ReportSnapshotSchemaVersion, ct);
        if (candidate.ReportVersion != report.Version
            || !string.Equals(candidate.ReportSnapshotHash, Hash(currentReportSnapshot), StringComparison.Ordinal))
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
        // The closure package is a second frozen boundary, so it must carry the same exact active
        // supporting-file manifest as the candidate and current output. Never fall back to the aggregate
        // serializer here: schema 6 without this manifest would advertise a package hash that omits files.
        var finalReportSnapshot = await CurrentReportSnapshotAsync(report, ct);
        if (candidate.ReportSnapshotSchemaVersion == ProblemReportEvidenceContract.SchemaVersion
            && !string.Equals(SupportingAttachmentManifestHash(candidate.ReportSnapshotJson),
                SupportingAttachmentManifestHash(finalReportSnapshot.Json), StringComparison.Ordinal))
            throw new InvalidOperationException("The pending closure candidate no longer matches the active supporting-file manifest.");
        var finalReportJson = finalReportSnapshot.Json;
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

    private async Task<(string Json, string Hash)> CurrentReportSnapshotAsync(ProblemReport report,
        CancellationToken ct)
        => await ProblemReportAttachmentEvidence.SnapshotAsync(db, report, ct);

    private async Task<string> ReportSnapshotForSchemaAsync(ProblemReport report, int schemaVersion,
        CancellationToken ct)
    {
        if (schemaVersion == ProblemReportEvidenceContract.SchemaVersion)
            return (await CurrentReportSnapshotAsync(report, ct)).Json;
        return ReportSnapshotForSchema(report, schemaVersion);
    }

    public static string ReportSnapshot(ProblemReport report) => ProblemReportEvidenceContract.Serialize(report);

    /// <summary>
    /// Recreates the bytes a candidate would have committed at the time its snapshot schema was current.
    ///
    /// This is intentionally a reader-side compatibility table. Current reports write schema 6 with the
    /// active attachment manifest through the async path above; old candidates remain byte-compatible with
    /// the historical aggregate contracts rather than being silently compared against today's shape.
    /// </summary>
    public static string ReportSnapshotForSchema(ProblemReport report, int schemaVersion) => schemaVersion switch
    {
        ProblemReportEvidenceContract.SchemaVersion => ReportSnapshot(report),
        // Schema 5 is a historical complete evidence envelope. Keep its reader-side projection even after
        // schema 6 added the active supporting-attachment manifest; revalidation must use the bytes and hash
        // contract that the candidate originally committed, never today's richer snapshot shape.
        5 => ProblemReportEvidenceContract.SerializeForSchema(report, 5),
        4 => ProblemReportEvidenceContract.SerializeForSchema(report, 4),
        3 => HistoricalV3ReportSnapshot(report),
        2 => HistoricalV2ReportSnapshot(report),
        1 => LegacyV1ReportSnapshot(report),
        _ => throw new InvalidOperationException($"Problem Report snapshot schema {schemaVersion} is not supported."),
    };

    private static string SupportingAttachmentManifestHash(string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        var manifest = document.RootElement.TryGetProperty("supportingAttachments", out var property)
            ? property.GetRawText()
            : "[]";
        return Hash(manifest);
    }

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
        type = LegacyProblemReportType(report.Category),
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
        state = LegacyAwaitingState(report.State),
        report.CreatedAt,
        report.UpdatedAt,
        report.Version,
    });

    private static string HistoricalV2ReportSnapshot(ProblemReport report) =>
        JsonSerializer.Serialize(new
        {
            contract = ProblemReportEvidenceContract.Contract,
            schemaVersion = 2,
            report.Id,
            report.ProjectId,
            report.ReportNumber,
            report.Revision,
            report.DisplayNumber,
            report.Title,
            report.Problem,
            report.Analysis,
            report.ReportedBy,
            report.ResponsibleEngineerId,
            report.TargetReleaseId,
            report.ProblemRich,
            report.AdditionalInformation,
            report.AdditionalInformationRich,
            report.SystemAircraftImpact,
            type = LegacyProblemReportType(report.Category),
            report.Workaround,
            report.ImpactAssessmentJson,
            report.Classification,
            severity = report.Severity.ToString(),
            priority = report.Priority.ToString(),
            report.Origin,
            report.AffectedConfiguration,
            report.RootCause,
            report.Effects,
            report.Containment,
            report.CorrectiveAction,
            disposition = report.Disposition?.ToString(),
            report.DispositionRationale,
            report.ResolutionVerificationExecutionId,
            report.ClosureApprovedBy,
            report.ClosureApprovedByName,
            report.ClosureApprovedAt,
            report.IsReleaseBlocker,
            report.ReleaseBlockerVersion,
            report.WaiverRationale,
            report.WaivedBy,
            report.WaivedAt,
            state = LegacyAwaitingState(report.State),
            report.CreatedAt,
            report.UpdatedAt,
            report.Version,
        }, HistoricalSnapshotJsonOptions);

    private static string HistoricalV3ReportSnapshot(ProblemReport report) =>
        JsonSerializer.Serialize(new
        {
            contract = ProblemReportEvidenceContract.Contract,
            schemaVersion = 3,
            report.Id,
            report.ProjectId,
            report.ReportNumber,
            report.Revision,
            report.DisplayNumber,
            report.Title,
            report.Problem,
            report.Analysis,
            report.ReportedBy,
            report.ResponsibleEngineerId,
            report.TargetReleaseId,
            report.ProblemRich,
            report.AdditionalInformation,
            report.AdditionalInformationRich,
            report.SystemAircraftImpact,
            category = report.Category?.ToString(),
            categoryProvenance = report.CategoryProvenance?.ToString(),
            report.Workaround,
            report.ImpactAssessmentJson,
            report.Classification,
            severity = report.Severity.ToString(),
            priority = report.Priority.ToString(),
            report.Origin,
            report.AffectedConfiguration,
            report.RootCause,
            report.Effects,
            report.Containment,
            report.CorrectiveAction,
            disposition = report.Disposition?.ToString(),
            report.DispositionRationale,
            report.ResolutionVerificationExecutionId,
            report.ClosureApprovedBy,
            report.ClosureApprovedByName,
            report.ClosureApprovedAt,
            report.IsReleaseBlocker,
            report.ReleaseBlockerVersion,
            report.WaiverRationale,
            report.WaivedBy,
            report.WaivedAt,
            state = ProblemReportTransitionPolicy.Canonical(report.State).ToString(),
            report.CreatedAt,
            report.UpdatedAt,
            report.Version,
        }, HistoricalSnapshotJsonOptions);

    private static string LegacyProblemReportType(ProblemReportCategory? category) => category switch
    {
        ProblemReportCategory.RequirementsDocumentation => "Documentation",
        ProblemReportCategory.CodeFunctional or ProblemReportCategory.CodeNonFunctional => "Code",
        ProblemReportCategory.TestBlocking or ProblemReportCategory.TestNonBlocking => "Test",
        _ => "Other",
    };

    private static string LegacyAwaitingState(ProblemReportState state) =>
        ProblemReportTransitionPolicy.Canonical(state) == ProblemReportState.WaitingForSqaToClose
            ? "AwaitingSqaClosure"
            : ProblemReportTransitionPolicy.Canonical(state).ToString();

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
