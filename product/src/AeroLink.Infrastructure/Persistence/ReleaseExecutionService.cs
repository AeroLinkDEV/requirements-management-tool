using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ReleaseReconciliationResult(int TraceLinksCreated, int CoverageLinksCreated, int UncoveredRequirements);
public sealed record VerificationManifestRow(Guid ProcedureRevisionId, string DisplayNumber, string Outcome, DateTimeOffset? ExecutedAt, string ExecutedBy, string Configuration, string Determination);
public sealed record VerificationImportResult(int ExecutionsRecorded, int Passed, int Failed, int Blocked, Guid EvidenceId, string EvidenceSha256);

public sealed class ReleaseExecutionService(AeroLinkDbContext db, EvidenceFileStore evidenceStore)
{
    public async Task<ReleaseReconciliationResult> ReconcileAsync(Guid campaignId, string actorId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var campaign = await db.ReleaseCampaigns.Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == campaignId, ct) ?? throw new DomainException("Release campaign not found.");
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

        var priorCoverage = await db.TestCoverage.AsNoTracking().Where(x => priorRevisionIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
        var existingCoverageKeys = (await db.TestCoverage.AsNoTracking().Where(x => current.Select(m => m.RevisionId).Contains(x.RequirementRevisionId))
            .Select(x => new { x.ProcedureRevisionId, x.RequirementRevisionId }).ToListAsync(ct))
            .Select(x => (x.ProcedureRevisionId, x.RequirementRevisionId)).ToHashSet();
        var coverageCreated = 0;
        foreach (var priorLink in priorCoverage)
        {
            if (!priorRevisionToArtifact.TryGetValue(priorLink.RequirementRevisionId, out var artifactId) || !currentByArtifact.TryGetValue(artifactId, out var requirementRevisionId)) continue;
            var key = (priorLink.ProcedureRevisionId, requirementRevisionId); if (existingCoverageKeys.Contains(key)) continue;
            db.TestCoverage.Add(new TestRequirementCoverage(priorLink.ProcedureRevisionId, requirementRevisionId)); existingCoverageKeys.Add(key); coverageCreated++;
        }
        var covered = existingCoverageKeys.Select(x => x.RequirementRevisionId).Distinct().Count(); var uncovered = current.Count - covered;
        campaign.RecordExecutionProgress("LifecycleLinksReconciled", $"Created {traceCreated} baseline-valid trace links and {coverageCreated} carried-forward coverage links; {uncovered} requirements still need explicit coverage.", actorId, now);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(traceCreated, coverageCreated, uncovered);
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
                var execution = new TestExecution(campaign.ProjectId, item.Row.ProcedureRevisionId, campaign.SoftwareBuildId, retest?.Id, item.Outcome!.Value, item.Row.ExecutedBy, item.Row.Configuration, item.Row.Determination, $"{stored.OriginalFileName} / SHA-256 {stored.Sha256}", item.Row.ExecutedAt!.Value, now);
                executions.Add(execution); db.TestExecutions.Add(execution); db.TestExecutionEvidence.Add(new TestExecutionEvidence(execution.Id, evidence.Id));
            }
            campaign.RecordExecutionProgress("VerificationPackageImported", $"Imported {executions.Count} build-specific results with evidence SHA-256 {stored.Sha256}.", actorId, now);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return new(executions.Count, executions.Count(x => x.Outcome == TestOutcome.Pass), executions.Count(x => x.Outcome == TestOutcome.Fail), executions.Count(x => x.Outcome == TestOutcome.Blocked), evidence.Id, evidence.Sha256);
        }
        catch { evidenceStore.Delete(stored.StorageKey); throw; }
    }

    private async Task<List<(Guid Id, string DisplayNumber)>> RequiredProceduresAsync(Guid baselineId, CancellationToken ct)
    {
        var revisionIds = await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId).Select(x => x.RevisionId).ToListAsync(ct);
        var procedureRevisionIds = await db.TestCoverage.AsNoTracking().Where(x => revisionIds.Contains(x.RequirementRevisionId)).Select(x => x.ProcedureRevisionId).Distinct().ToListAsync(ct);
        return await (from revision in db.TestProcedureRevisions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.Id)) join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id orderby procedure.BaseNumber select new ValueTuple<Guid, string>(revision.Id, procedure.BaseNumber + "." + revision.Revision.ToString("D2"))).ToListAsync(ct);
    }
}
