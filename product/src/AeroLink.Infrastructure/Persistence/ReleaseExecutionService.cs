using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary><paramref name="SuspectCoverage"/> counts requirement revisions whose only coverage was carried
/// forward across a change and has not been reconfirmed. It is not coverage for release purposes.</summary>
public sealed record ReleaseReconciliationResult(int TraceLinksCreated, int SuspectCoverage, int UncoveredRequirements);
public sealed record VerificationManifestRow(Guid ProcedureRevisionId, string DisplayNumber, string Outcome, DateTimeOffset? ExecutedAt, string ExecutedBy, string Configuration, string Determination);
public sealed record VerificationImportResult(int ExecutionsRecorded, int Passed, int Failed, int Blocked, Guid EvidenceId, string EvidenceSha256);

public sealed class ReleaseExecutionService(AeroLinkDbContext db, EvidenceFileStore evidenceStore)
{
    public async Task<ReleaseReconciliationResult> ReconcileAsync(Guid campaignId, string actorId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var campaign = await db.ReleaseCampaigns.Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == campaignId, ct) ?? throw new DomainException("Release campaign not found.");
        if (campaign.State == ReleaseCampaignState.InReview) throw new DomainException("The release package is frozen while approval is in progress.");
        if (campaign.State == ReleaseCampaignState.Released) throw new DomainException("A released campaign is immutable.");
        var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == campaign.BaselineId, ct);
        if (baseline.RequirementsMaterializedAt is null) throw new DomainException("Materialize the release baseline before reconciling lifecycle links.");
        if (baseline.PredecessorBaselineId is null) throw new DomainException("A predecessor baseline is required for controlled link carry-forward.");

        var current = await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id).ToListAsync(ct);
        var prior = await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.PredecessorBaselineId).ToListAsync(ct);
        var currentByArtifact = current.ToDictionary(x => x.ArtifactId, x => x.RevisionId);
        var priorRevisionToArtifact = prior.ToDictionary(x => x.RevisionId, x => x.ArtifactId);
        var priorRevisionIds = prior.Select(x => x.RevisionId).ToList();

        var existingTraceKeys = (await db.RequirementTraces.AsNoTracking().Where(x => x.ProjectId == campaign.ProjectId)
            .Select(x => new { x.SourceRevisionId, x.TargetRevisionId, x.Type }).ToListAsync(ct))
            .Select(x => (x.SourceRevisionId, x.TargetRevisionId, x.Type)).ToHashSet();
        var priorTraces = await db.RequirementTraces.AsNoTracking().Where(x => priorRevisionIds.Contains(x.SourceRevisionId) && priorRevisionIds.Contains(x.TargetRevisionId)).ToListAsync(ct);
        var traceCreated = 0;
        foreach (var priorTrace in priorTraces)
        {
            if (!currentByArtifact.TryGetValue(priorRevisionToArtifact[priorTrace.SourceRevisionId], out var source) || !currentByArtifact.TryGetValue(priorRevisionToArtifact[priorTrace.TargetRevisionId], out var target)) continue;
            var key = (source, target, priorTrace.Type); if (existingTraceKeys.Contains(key)) continue;
            db.RequirementTraces.Add(new RequirementTraceLink(campaign.ProjectId, source, target, priorTrace.Type, $"Carried forward from the predecessor baseline during {campaign.Name}; confirm impact disposition provides approval rationale.", now));
            existingTraceKeys.Add(key); traceCreated++;
        }

        // Coverage carry-forward belongs to materialisation, which marks a link suspect when the requirement
        // changed under the procedure. This step used to carry the same links forward itself and leave them
        // unmarked, which asserted that a procedure written against the previous wording still verified the
        // new one — the precise claim nobody had made. Reconciliation now reports that state instead of
        // manufacturing it.
        var currentRevisionIds = current.Select(x => x.RevisionId).ToList();
        var currentCoverage = await db.TestCoverage.AsNoTracking()
            .Where(x => currentRevisionIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
        var suspect = currentCoverage.Where(x => x.IsSuspect).Select(x => x.RequirementRevisionId).Distinct().Count();
        var covered = currentCoverage.Where(x => !x.IsSuspect).Select(x => x.RequirementRevisionId).Distinct().Count();
        var uncovered = current.Count - covered;
        campaign.RecordExecutionProgress("LifecycleLinksReconciled", $"Created {traceCreated} baseline-valid trace links; {suspect} requirement revisions carry suspect coverage awaiting verification confirmation and {uncovered} still need confirmed coverage.", actorId, now);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(traceCreated, suspect, uncovered);
    }

    public async Task<byte[]> CreateVerificationTemplateAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == campaignId, ct) ?? throw new DomainException("Release campaign not found.");
        if (campaign.SoftwareBuildId is null) throw new DomainException("Select the exact verification build before exporting the manifest.");
        var rows = await RequiredProceduresAsync(campaign.BaselineId, ct);
        var template = rows.Select(x => new VerificationManifestRow(x.Id, x.DisplayNumber, "REQUIRED: Pass, Fail, or Blocked", null, "", "", "")).ToList();
        return JsonSerializer.SerializeToUtf8Bytes(template, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<VerificationImportResult> ImportVerificationAsync(Guid campaignId, Stream manifestStream, Stream evidenceStream, string evidenceFileName, string evidenceContentType, string actorId, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("The verification import owner is required.");
        var campaign = await db.ReleaseCampaigns.Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == campaignId, ct) ?? throw new DomainException("Release campaign not found.");
        if (campaign.State == ReleaseCampaignState.InReview) throw new DomainException("The release package is frozen while approval is in progress.");
        if (campaign.State == ReleaseCampaignState.Released) throw new DomainException("A released campaign is immutable.");
        if (campaign.SoftwareBuildId is null) throw new DomainException("Select the exact verification build before importing results.");
        List<VerificationManifestRow> manifest;
        try { manifest = await JsonSerializer.DeserializeAsync<List<VerificationManifestRow>>(manifestStream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct) ?? []; }
        catch (JsonException) { throw new DomainException("The verification manifest is not valid JSON."); }
        var required = await RequiredProceduresAsync(campaign.BaselineId, ct); var requiredIds = required.Select(x => x.Id).ToHashSet();
        if (requiredIds.Count == 0) throw new DomainException("The baseline has no required covered procedures to import.");
        if (manifest.Count != requiredIds.Count || manifest.Select(x => x.ProcedureRevisionId).Distinct().Count() != manifest.Count || manifest.Any(x => !requiredIds.Contains(x.ProcedureRevisionId)))
            throw new DomainException($"The manifest must contain exactly one result for each of the {requiredIds.Count} required procedure revisions.");
        var parsed = manifest.Select(x => (Row: x, Outcome: Enum.TryParse<TestOutcome>(x.Outcome, true, out var value) ? value : (TestOutcome?)null)).ToList();
        if (parsed.Any(x => x.Outcome is null || x.Row.ExecutedAt is null || string.IsNullOrWhiteSpace(x.Row.ExecutedBy) || string.IsNullOrWhiteSpace(x.Row.Determination)))
            throw new DomainException("Every row requires a valid outcome, execution time, executor, and human determination.");

        var stored = await evidenceStore.StoreAsync(evidenceStream, evidenceFileName, evidenceContentType, ct);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var evidence = new EvidenceRecord(campaign.ProjectId, stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256, stored.StorageKey, actorId, now); db.EvidenceRecords.Add(evidence);
            var prior = await db.TestExecutions.Where(x => x.SoftwareBuildId == campaign.SoftwareBuildId && requiredIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
            var executions = new List<TestExecution>();
            foreach (var item in parsed)
            {
                var retest = prior.Where(x => x.ProcedureRevisionId == item.Row.ProcedureRevisionId).OrderByDescending(x => x.ExecutedAt).ThenByDescending(x => x.RecordedAt).FirstOrDefault();
                var execution = new TestExecution(campaign.ProjectId, item.Row.ProcedureRevisionId, campaign.SoftwareBuildId, retest?.Id, item.Outcome!.Value, item.Row.ExecutedBy, item.Row.Configuration, item.Row.Determination, $"{stored.OriginalFileName} / SHA-256 {stored.Sha256}", item.Row.ExecutedAt!.Value, now, campaign.ReleaseId);
                executions.Add(execution); db.TestExecutions.Add(execution); db.TestExecutionEvidence.Add(new TestExecutionEvidence(execution.Id, evidence.Id));
            }
            campaign.RecordExecutionProgress("VerificationPackageImported", $"Imported {executions.Count} build-specific results with evidence SHA-256 {stored.Sha256}.", actorId, now);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return new(executions.Count, executions.Count(x => x.Outcome == TestOutcome.Pass), executions.Count(x => x.Outcome == TestOutcome.Fail), executions.Count(x => x.Outcome == TestOutcome.Blocked), evidence.Id, evidence.Sha256);
        }
        catch { evidenceStore.Delete(stored.StorageKey); throw; }
    }

    public async Task<string> ComputeReviewManifestHashAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == campaignId, ct)
            ?? throw new DomainException("Release campaign not found.");
        if (campaign.SoftwareBuildId is null) throw new DomainException("Select the exact verification build before freezing the release package.");

        var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == campaign.BaselineId, ct);
        var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == campaign.ReleaseId, ct);
        var build = await db.SoftwareBuilds.AsNoTracking().SingleAsync(x => x.Id == campaign.SoftwareBuildId, ct);
        var members = await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id).ToListAsync(ct);
        var revisionIds = members.Select(x => x.RevisionId).ToHashSet();
        var selections = await db.BaselineSelections.AsNoTracking().Where(x => x.BaselineId == baseline.Id).ToListAsync(ct);
        var selectedScrIds = selections.Select(x => x.ScrId).ToList();
        var changes = await db.SystemChangeRequests.AsNoTracking().Where(x => selectedScrIds.Contains(x.Id)).ToListAsync(ct);
        var impacts = await db.ImpactDispositions.AsNoTracking().Where(x => x.CampaignId == campaign.Id).ToListAsync(ct);
        var traces = await db.RequirementTraces.AsNoTracking().Where(x => x.ProjectId == campaign.ProjectId && (revisionIds.Contains(x.SourceRevisionId) || revisionIds.Contains(x.TargetRevisionId))).ToListAsync(ct);
        var coverage = await db.TestCoverage.AsNoTracking().Where(x => revisionIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
        var procedureRevisionIds = coverage.Select(x => x.ProcedureRevisionId).Distinct().ToList();
        var procedureRevisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.Id)).ToListAsync(ct);
        var procedureIds = procedureRevisions.Select(x => x.ProcedureId).Distinct().ToList();
        var procedures = await db.TestProcedures.AsNoTracking().Where(x => procedureIds.Contains(x.Id)).ToListAsync(ct);
        var executions = await db.TestExecutions.AsNoTracking().Where(x => x.SoftwareBuildId == campaign.SoftwareBuildId && procedureRevisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
        var executionIds = executions.Select(x => x.Id).ToList();
        var evidenceLinks = await db.TestExecutionEvidence.AsNoTracking().Where(x => executionIds.Contains(x.TestExecutionId)).ToListAsync(ct);
        var evidenceIds = evidenceLinks.Select(x => x.EvidenceId).Distinct().ToList();
        var evidence = await db.EvidenceRecords.AsNoTracking().Where(x => evidenceIds.Contains(x.Id)).ToListAsync(ct);
        var documents = await db.ControlledDocuments.AsNoTracking().Where(x => x.BaselineId == baseline.Id).ToListAsync(ct);
        var codeTraceability = await db.CodeTraceabilityRecords.AsNoTracking()
            .Where(x => x.ProjectId == campaign.ProjectId && x.ReleaseId == campaign.ReleaseId && revisionIds.Contains(x.RequirementRevisionId))
            .ToListAsync(ct);

        var canonical = JsonSerializer.Serialize(new
        {
            schema = "aerolink.release-review-manifest.v1",
            campaign = new { campaign.Id, campaign.ProjectId, campaign.ReleaseId, campaign.BaselineId, campaign.SoftwareBuildId },
            release = new { release.Id, release.Version, release.PredecessorReleaseId },
            baseline = new { baseline.Id, baseline.DisplayNumber, baseline.PredecessorBaselineId, baseline.ContentHash, baseline.RequirementsHash, baseline.RequirementsMaterializedAt },
            build = new { build.Id, build.BuildNumber, build.Description, build.RecordedBy, build.RecordedAt },
            members = members.OrderBy(x => x.ArtifactId).ThenBy(x => x.RevisionId).Select(x => new { x.ArtifactId, x.RevisionId }),
            changes = changes.OrderBy(x => x.BaseNumber).ThenBy(x => x.Revision).Select(x => new { x.Id, x.BaseNumber, x.Revision, state = x.State.ToString(), x.Version, x.UpdatedAt }),
            impacts = impacts.OrderBy(x => x.ScrId).ThenBy(x => x.Kind).ThenBy(x => x.Id).Select(x => new { x.Id, x.ScrId, kind = x.Kind.ToString(), state = x.State.ToString(), x.Rationale, x.DispositionedBy, x.DispositionedAt }),
            traces = traces.OrderBy(x => x.SourceRevisionId).ThenBy(x => x.TargetRevisionId).ThenBy(x => x.Type).Select(x => new { x.Id, x.SourceRevisionId, x.TargetRevisionId, type = x.Type.ToString(), x.Rationale }),
            coverage = coverage.OrderBy(x => x.RequirementRevisionId).ThenBy(x => x.ProcedureRevisionId).Select(x => new { x.RequirementRevisionId, x.ProcedureRevisionId }),
            procedures = (from revision in procedureRevisions join procedure in procedures on revision.ProcedureId equals procedure.Id orderby procedure.BaseNumber, revision.Revision select new { procedure.Id, procedure.BaseNumber, procedure.Title, revisionId = revision.Id, revision.Revision, state = revision.State.ToString(), revision.Objective, revision.Preconditions, revision.Steps, revision.ExpectedResult }),
            executions = executions.OrderBy(x => x.ProcedureRevisionId).ThenBy(x => x.ExecutedAt).ThenBy(x => x.Id).Select(x => new { x.Id, x.ProcedureRevisionId, x.SoftwareBuildId, x.RetestOfExecutionId, outcome = x.Outcome.ToString(), x.ExecutedBy, x.Configuration, x.Determination, x.EvidenceReference, x.ExecutedAt, x.RecordedAt }),
            evidence = (from link in evidenceLinks join item in evidence on link.EvidenceId equals item.Id orderby link.TestExecutionId, item.Id select new { link.TestExecutionId, item.Id, item.OriginalFileName, item.ContentType, item.Size, item.Sha256, item.UploadedBy, item.UploadedAt }),
            codeTraceability = codeTraceability.OrderBy(x => x.RequirementRevisionId).Select(x => new
            {
                x.Id,
                x.RequirementArtifactId,
                x.RequirementRevisionId,
                disposition = x.Disposition.ToString(),
                x.RepositoryPath,
                x.MergeRequestReference,
                x.MergeRequestTitle,
                x.MergeRequestUrl,
                x.MergeCommitSha,
                x.MergedAt,
                x.NoCodeChangeRationale,
                x.IsDemonstration,
                x.RecordedBy,
                x.RecordedAt,
            }),
            documents = documents.OrderBy(x => x.Type).ThenBy(x => x.DocumentNumber).ThenBy(x => x.Revision).Select(x => new { x.Id, type = x.Type.ToString(), x.DocumentNumber, x.Revision, x.ContentHash, x.ArtifactCount, x.GeneratedAt })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private async Task<List<(Guid Id, string DisplayNumber)>> RequiredProceduresAsync(Guid baselineId, CancellationToken ct)
    {
        var revisionIds = await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId).Select(x => x.RevisionId).ToListAsync(ct);
        var procedureRevisionIds = await db.TestCoverage.AsNoTracking().Where(x => revisionIds.Contains(x.RequirementRevisionId)).Select(x => x.ProcedureRevisionId).Distinct().ToListAsync(ct);
        return await (from revision in db.TestProcedureRevisions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.Id)) join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id orderby procedure.BaseNumber select new ValueTuple<Guid, string>(revision.Id, procedure.BaseNumber + "." + revision.Revision.ToString("D2"))).ToListAsync(ct);
    }
}
