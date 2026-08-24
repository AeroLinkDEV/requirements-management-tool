using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary><paramref name="SuspectCoverage"/> counts requirement revisions whose only coverage was carried
/// forward across a change and has not been reconfirmed. It is not coverage for release purposes.</summary>
public sealed record ReleaseReconciliationResult(int TraceLinksCreated, int SuspectCoverage, int UncoveredRequirements);
public sealed record VerificationManifestRow(Guid ProcedureRevisionId, string DisplayNumber, string Outcome, DateTimeOffset? ExecutedAt, string ExecutedBy, string Configuration, string Determination);
public sealed record VerificationImportResult(int ExecutionsRecorded, int Passed, int Failed, int Blocked, Guid EvidenceId, string EvidenceSha256);

public sealed class ReleaseExecutionService(AeroLinkDbContext db, EvidenceFileStore evidenceStore,
    IProjectLadderPolicyResolver? policyResolver = null)
{
    public async Task<ReleaseReconciliationResult> ReconcileAsync(Guid campaignId, string actorId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var campaign = await db.ReleaseCampaigns.Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == campaignId, ct) ?? throw new DomainException("Release campaign not found.");
        var ladderPolicy = policyResolver is null
            ? LegacyLadderPolicy.Instance
            : await policyResolver.ResolveAsync(campaign.ProjectId, ct);
        if (campaign.State == ReleaseCampaignState.InReview) throw new DomainException("The release package is frozen while approval is in progress.");
        if (campaign.State == ReleaseCampaignState.Released) throw new DomainException("A released campaign is immutable.");
        var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == campaign.BaselineId, ct);
        if (baseline.RequirementsMaterializedAt is null) throw new DomainException("Materialize the release baseline before reconciling lifecycle links.");
        if (baseline.PredecessorBaselineId is null) throw new DomainException("A predecessor baseline is required for controlled link carry-forward.");

        // Coverage carry-forward belongs to materialisation, which marks a link suspect when the requirement
        // changed under the procedure. This step used to carry the same links forward itself and leave them
        // unmarked, which asserted that a procedure written against the previous wording still verified the
        // new one — the precise claim nobody had made. Reconciliation now reports that state instead of
        // manufacturing it.
        var verificationLevels = ladderPolicy.Definitions
            .Where(x => x.Verification is not null)
            .Select(x => x.Level)
            .ToHashSet();
        var coverageRevisionIds = await (from member in db.BaselineRequirements.AsNoTracking()
                                         join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                                         where member.BaselineId == baseline.Id && verificationLevels.Contains(artifact.Level)
                                         select member.RevisionId).ToListAsync(ct);
        var currentCoverage = await db.TestCoverage.AsNoTracking()
            .Where(x => coverageRevisionIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
        if (ladderPolicy is not ILegacyLadderCompatibilityPolicy && currentCoverage.Count != 0)
        {
            var allowedProcedureLevels = ladderPolicy.Definitions.Where(x => x.Verification is not null)
                .Select(x => x.Verification!.ProcedureLevel).ToHashSet();
            var candidateProcedureIds = currentCoverage.Select(x => x.ProcedureRevisionId).Distinct().ToList();
            var allowedProcedureIds = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                             join procedure in db.TestProcedures.AsNoTracking().Where(x => x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case) on revision.ProcedureId equals procedure.Id
                                             where candidateProcedureIds.Contains(revision.Id)
                                                 && procedure.ProjectId == campaign.ProjectId
                                                 && allowedProcedureLevels.Contains(procedure.Level)
                                             select revision.Id).ToListAsync(ct);
            currentCoverage = currentCoverage.Where(x => allowedProcedureIds.Contains(x.ProcedureRevisionId)).ToList();
        }
        var suspect = currentCoverage.Where(x => x.IsSuspect).Select(x => x.RequirementRevisionId).Distinct().Count();
        var covered = currentCoverage.Where(x => !x.IsSuspect).Select(x => x.RequirementRevisionId).Distinct().Count();
        var uncovered = coverageRevisionIds.Count - covered;
        campaign.RecordExecutionProgress("LifecycleLinksReconciled", $"Baseline materialization owns exact trace carry-forward; {suspect} requirement revisions carry suspect coverage awaiting verification confirmation and {uncovered} still need confirmed coverage.", actorId, now);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(0, suspect, uncovered);
    }

    private static bool IsConfiguredTrace(ILadderPolicy policy, RequirementLevel source, RequirementLevel target,
        RequirementTraceType type)
    {
        try { RequirementTracePolicy.Validate(policy, source, target, type); return true; }
        catch (DomainException) { return false; }
    }

    private static bool IsConfiguredChangeRequest(ILadderPolicy policy, SystemChangeRequest request)
    {
        try
        {
            return policy.IsChangeRequestScopeValid(request.Type, request.SoftwareLevel);
        }
        catch (DomainException) { return false; }
    }

    public async Task<byte[]> CreateVerificationTemplateAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == campaignId, ct) ?? throw new DomainException("Release campaign not found.");
        if (campaign.SoftwareBuildId is null) throw new DomainException("Select the exact verification build before exporting the manifest.");
        var ladderPolicy = policyResolver is null
            ? LegacyLadderPolicy.Instance
            : await policyResolver.ResolveAsync(campaign.ProjectId, ct);
        var rows = await RequiredProceduresAsync(campaign.ProjectId, campaign.BaselineId, ladderPolicy, ct);
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
        var ladderPolicy = policyResolver is null
            ? LegacyLadderPolicy.Instance
            : await policyResolver.ResolveAsync(campaign.ProjectId, ct);
        var required = await RequiredProceduresAsync(campaign.ProjectId, campaign.BaselineId, ladderPolicy, ct); var requiredIds = required.Select(x => x.Id).ToHashSet();
        if (requiredIds.Count == 0) throw new DomainException("The baseline has no required covered verification artifacts to import.");
        if (manifest.Count != requiredIds.Count || manifest.Select(x => x.ProcedureRevisionId).Distinct().Count() != manifest.Count || manifest.Any(x => !requiredIds.Contains(x.ProcedureRevisionId)))
            throw new DomainException($"The manifest must contain exactly one result for each of the {requiredIds.Count} required verification artifact revisions.");
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
        var ladderPolicy = policyResolver is null
            ? LegacyLadderPolicy.Instance
            : await policyResolver.ResolveAsync(campaign.ProjectId, ct);
        var configuredLevels = ladderPolicy.OrderedLevels.ToHashSet();
        var verificationLevels = ladderPolicy.Definitions.Where(x => x.Verification is not null)
            .Select(x => x.Level).ToHashSet();
        var configuredProcedureLevels = ladderPolicy.Definitions.Where(x => x.Verification is not null)
            .Select(x => x.Verification!.ProcedureLevel).ToHashSet();
        var configuredDisciplines = ladderPolicy.Definitions.Where(x => x.Verification is not null)
            .Select(x => x.Verification!.Discipline).ToHashSet();
        var configuredDocumentTypes = ladderPolicy.ControlledDocumentTypes.ToHashSet();
        var members = await (from member in db.BaselineRequirements.AsNoTracking()
                             join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                             where member.BaselineId == baseline.Id && configuredLevels.Contains(artifact.Level)
                             select member).ToListAsync(ct);
        var revisionIds = members.Select(x => x.RevisionId).ToHashSet();
        var revisionLevels = await (from member in db.BaselineRequirements.AsNoTracking()
                                    join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                                    where member.BaselineId == baseline.Id && configuredLevels.Contains(artifact.Level)
                                    select new { member.RevisionId, artifact.Level }).ToDictionaryAsync(x => x.RevisionId, x => x.Level, ct);
        var selections = await db.BaselineSelections.AsNoTracking().Where(x => x.BaselineId == baseline.Id).ToListAsync(ct);
        var selectedScrIds = selections.Select(x => x.ChangeRequestId).ToList();
        var changes = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => selectedScrIds.Contains(x.Id)).ToListAsync(ct);
        var impacts = await db.ImpactDispositions.AsNoTracking()
            .Where(x => x.CampaignId == campaign.Id).ToListAsync(ct);
        var traces = await db.RequirementTraces.AsNoTracking()
            .Where(x => x.ProjectId == campaign.ProjectId
                && revisionIds.Contains(x.SourceRevisionId) && revisionIds.Contains(x.TargetRevisionId))
            .ToListAsync(ct);
        if (ladderPolicy is not ILegacyLadderCompatibilityPolicy)
        {
            changes = changes.Where(x => IsConfiguredChangeRequest(ladderPolicy, x)).ToList();
            var selectedChangeIds = changes.Select(x => x.Id).ToHashSet();
            impacts = impacts.Where(x => selectedChangeIds.Contains(x.ChangeRequestId)).ToList();
            var endpointIds = traces.SelectMany(x => new[] { x.SourceRevisionId, x.TargetRevisionId }).Distinct().ToList();
            var endpointLevels = await (from revision in db.RequirementRevisions.AsNoTracking()
                                        join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                        where endpointIds.Contains(revision.Id)
                                        select new { revision.Id, artifact.Level }).ToDictionaryAsync(x => x.Id, x => x.Level, ct);
            traces = traces.Where(x => endpointLevels.TryGetValue(x.SourceRevisionId, out var source)
                && endpointLevels.TryGetValue(x.TargetRevisionId, out var target)
                && IsConfiguredTrace(ladderPolicy, source, target, x.Type)).ToList();
        }
        var traceLinkIds = traces.Select(x => x.Id).ToList();
        var traceLifecycles = await db.ExactLinkSuspectLifecycles.AsNoTracking()
            .Where(x => x.LinkKind == ExactLinkKind.RequirementTrace && traceLinkIds.Contains(x.LinkId))
            .ToDictionaryAsync(x => x.LinkId, ct);
        var traceLifecycleIds = traceLifecycles.Values.Select(x => x.Id).ToList();
        var traceLifecycleEvents = (await db.ExactLinkSuspectEvents.AsNoTracking()
            .Where(x => traceLifecycleIds.Contains(x.LifecycleId)).ToListAsync(ct))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToList();
        var coverageRevisionIds = revisionLevels.Where(x => verificationLevels.Contains(x.Value)).Select(x => x.Key).ToHashSet();
        var coverage = await db.TestCoverage.AsNoTracking().Where(x => coverageRevisionIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
        var procedureRevisionIds = coverage.Select(x => x.ProcedureRevisionId).Distinct().ToList();
        if (ladderPolicy is not ILegacyLadderCompatibilityPolicy && procedureRevisionIds.Count != 0)
        {
            var allowedProcedureRevisionIds = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                                     join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                                                 where procedureRevisionIds.Contains(revision.Id)
                                                         && procedure.ProjectId == campaign.ProjectId
                                                         && configuredProcedureLevels.Contains(procedure.Level)
                                                         && (procedure.Level == TestProcedureLevel.System || procedure.ArtifactKind == VerificationArtifactKind.Case)
                                                     select revision.Id).ToListAsync(ct);
            coverage = coverage.Where(x => allowedProcedureRevisionIds.Contains(x.ProcedureRevisionId)).ToList();
            procedureRevisionIds = allowedProcedureRevisionIds;
        }
        var procedureRevisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.Id)).ToListAsync(ct);
        var procedureTitles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
            procedureRevisionIds, ct);
        var procedureIds = procedureRevisions.Select(x => x.ProcedureId).Distinct().ToList();
        var procedures = await db.TestProcedures.AsNoTracking().Where(x => procedureIds.Contains(x.Id)).ToListAsync(ct);
        var executions = await db.TestExecutions.AsNoTracking().Where(x => x.SoftwareBuildId == campaign.SoftwareBuildId && procedureRevisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
        var executionIds = executions.Select(x => x.Id).ToList();
        var evidenceLinks = await db.TestExecutionEvidence.AsNoTracking().Where(x => executionIds.Contains(x.TestExecutionId)).ToListAsync(ct);
        var evidenceIds = evidenceLinks.Select(x => x.EvidenceId).Distinct().ToList();
        var evidence = await db.EvidenceRecords.AsNoTracking().Where(x => evidenceIds.Contains(x.Id)).ToListAsync(ct);
        var documents = await db.ControlledDocuments.AsNoTracking()
            .Where(x => x.BaselineId == baseline.Id && configuredDocumentTypes.Contains(x.Type)).ToListAsync(ct);
        var codeTraceability = await db.CodeTraceabilityRecords.AsNoTracking()
            .Where(x => x.ProjectId == campaign.ProjectId && x.ReleaseId == campaign.ReleaseId && revisionIds.Contains(x.RequirementRevisionId))
            .ToListAsync(ct);
        if (ladderPolicy is not ILegacyLadderCompatibilityPolicy)
            codeTraceability = codeTraceability.Where(x => revisionLevels.TryGetValue(x.RequirementRevisionId, out var level)
                && ladderPolicy.HasCodeTraceability(level)).ToList();
        var testSet = await (from entry in db.BuildTestSetEntries.AsNoTracking()
                             join set in db.BuildTestSets.AsNoTracking() on entry.BuildTestSetId equals set.Id
                             where set.ReleaseId == campaign.ReleaseId && configuredDisciplines.Contains(set.Discipline)
                             orderby entry.BuildTestSetId, entry.ProcedureRevisionId
                             select new { entry.BuildTestSetId, entry.ProcedureRevisionId }).ToListAsync(ct);
        if (testSet.Count != 0)
        {
            var testSetProcedureIds = testSet.Select(x => x.ProcedureRevisionId).ToHashSet();
            var executableBindings = EffectiveExecutableArtifact.Bindings(ladderPolicy);
            var allowedTestSetProcedureIds = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                                    join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                                                    where testSetProcedureIds.Contains(revision.Id) && configuredProcedureLevels.Contains(procedure.Level)
                                                        && executableBindings.Any(binding =>
                                                            binding.Level == procedure.Level
                                                            && binding.Kind == procedure.ArtifactKind)
                                                    select revision.Id).ToListAsync(ct);
            testSet = testSet.Where(x => allowedTestSetProcedureIds.Contains(x.ProcedureRevisionId)).ToList();
        }
        var testChangeReviews = (await db.TestChangeReviews.AsNoTracking()
                .Where(x => x.ReleaseId == campaign.ReleaseId && configuredDisciplines.Contains(x.Discipline))
                .Select(x => new { x.Id, x.BaseNumber, x.Revision, state = x.State.ToString(), x.AssignedEngineerId, x.UpdatedAt })
                .ToListAsync(ct))
            .OrderBy(x => x.BaseNumber).ThenBy(x => x.Revision)
            .Select(x => new { x.Id, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.state, x.AssignedEngineerId, x.UpdatedAt })
            .ToList();

        var canonical = JsonSerializer.Serialize(new
        {
            schema = "aerolink.release-review-manifest.v1",
            campaign = new { campaign.Id, campaign.ProjectId, campaign.ReleaseId, campaign.BaselineId, campaign.SoftwareBuildId },
            release = new { release.Id, release.Version, release.PredecessorReleaseId },
            baseline = new { baseline.Id, baseline.DisplayNumber, baseline.PredecessorBaselineId, baseline.ContentHash, baseline.RequirementsHash, baseline.RequirementsMaterializedAt, baseline.TestProceduresHash, baseline.TestProceduresMaterializedAt },
            build = new { build.Id, build.BuildNumber, build.Description, build.RecordedBy, build.RecordedAt },
            members = members.OrderBy(x => x.ArtifactId).ThenBy(x => x.RevisionId).Select(x => new { x.ArtifactId, x.RevisionId }),
            changes = changes.OrderBy(x => x.BaseNumber).ThenBy(x => x.Revision).Select(x => new { x.Id, x.BaseNumber, x.Revision, state = x.State.ToString(), x.Version, x.UpdatedAt }),
            impacts = impacts.OrderBy(x => x.ChangeRequestId).ThenBy(x => x.Kind).ThenBy(x => x.Id).Select(x => new { x.Id, x.ChangeRequestId, kind = x.Kind.ToString(), state = x.State.ToString(), x.Rationale, x.DispositionedBy, x.DispositionedAt }),
            traces = traces.OrderBy(x => x.SourceRevisionId).ThenBy(x => x.TargetRevisionId).ThenBy(x => x.Type).Select(x =>
            {
                traceLifecycles.TryGetValue(x.Id, out var lifecycle);
                return new
                {
                    x.Id, x.SourceRevisionId, x.TargetRevisionId, type = x.Type.ToString(), x.Rationale,
                    lifecycle = lifecycle is null ? null : new
                    {
                        linkKind = lifecycle.LinkKind.ToString(), state = lifecycle.State.ToString(),
                        causeKind = lifecycle.CauseKind.ToString(), lifecycle.CauseRequirementRevisionId,
                        lifecycle.CauseBaselineImportId, lifecycle.RaisedBy, lifecycle.RaisedAt,
                        lifecycle.RaisedRationale, lifecycle.AcknowledgedBy, lifecycle.AcknowledgedAt,
                        lifecycle.AcknowledgementRationale, outcome = lifecycle.Outcome?.ToString(),
                        lifecycle.ResolvedBy, lifecycle.ResolvedAt, lifecycle.ResolutionRationale,
                        events = traceLifecycleEvents.Where(e => e.LifecycleId == lifecycle.Id).Select(e => new
                        {
                            type = e.EventType.ToString(), e.ActorId, e.OccurredAt, e.Rationale,
                            causeKind = e.CauseKind.ToString(), e.CauseRequirementRevisionId,
                            e.CauseBaselineImportId, outcome = e.Outcome?.ToString()
                        })
                    }
                };
            }),
            coverage = coverage.OrderBy(x => x.RequirementRevisionId).ThenBy(x => x.ProcedureRevisionId).Select(x => new { x.RequirementRevisionId, x.ProcedureRevisionId }),
            procedures = (from revision in procedureRevisions join procedure in procedures on revision.ProcedureId equals procedure.Id orderby procedure.BaseNumber, revision.Revision select new { procedure.Id, procedure.BaseNumber, title = procedureTitles[revision.Id].Title, revisionId = revision.Id, revision.Revision, state = revision.State.ToString(), revision.Objective, revision.Preconditions, revision.Steps, revision.ExpectedResult }),
            executions = executions.OrderBy(x => x.ProcedureRevisionId).ThenBy(x => x.ExecutedAt).ThenBy(x => x.Id).Select(x => new { x.Id, x.ProcedureRevisionId, x.SoftwareBuildId, x.RetestOfExecutionId, outcome = x.Outcome.ToString(), x.ExecutedBy, x.Configuration, x.Determination, x.EvidenceReference, x.ExecutedAt, x.RecordedAt }),
            evidence = (from link in evidenceLinks join item in evidence on link.EvidenceId equals item.Id orderby link.TestExecutionId, item.Id select new { link.TestExecutionId, item.Id, item.OriginalFileName, item.ContentType, item.Size, item.Sha256, item.UploadedBy, item.UploadedAt }),
            testSet,
            testChangeReviews,
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

    private async Task<List<(Guid Id, string DisplayNumber)>> RequiredProceduresAsync(Guid projectId, Guid baselineId,
        ILadderPolicy ladderPolicy, CancellationToken ct)
    {
        if (ladderPolicy is ILegacyLadderCompatibilityPolicy)
        {
            var revisionIds = await db.BaselineRequirements.AsNoTracking()
                .Where(x => x.BaselineId == baselineId).Select(x => x.RevisionId).ToListAsync(ct);
            var legacyProcedureRevisionIds = await db.TestCoverage.AsNoTracking()
                .Where(x => revisionIds.Contains(x.RequirementRevisionId))
                .Select(x => x.ProcedureRevisionId).Distinct().ToListAsync(ct);
            return await (from revision in db.TestProcedureRevisions.AsNoTracking().Where(x => legacyProcedureRevisionIds.Contains(x.Id))
                          join procedure in db.TestProcedures.AsNoTracking().Where(x => x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case) on revision.ProcedureId equals procedure.Id
                          orderby procedure.BaseNumber
                          select new ValueTuple<Guid, string>(revision.Id, procedure.BaseNumber + "." + revision.Revision.ToString("D2"))).ToListAsync(ct);
        }
        var configuredRequirementLevels = ladderPolicy.Definitions.Where(x => x.Verification is not null)
            .Select(x => x.Level).ToHashSet();
        var executableBindings = EffectiveExecutableArtifact.Bindings(ladderPolicy);
        // #726: the required manifest is the set of EFFECTIVE EXECUTABLE artifacts. With the software
        // Procedure tier enabled, the required executables are the Procedure revisions linked to the
        // baseline's exact Case revisions; Case-only software and System keep their current coverage rule.
        var procedureEnabledLevels = ladderPolicy.OrderedLevels
            .Where(level => ladderPolicy.Definition(level).Verification is not null
                && ladderPolicy.VerificationProfile(level).Enables(VerificationArtifactKind.Procedure))
            .ToHashSet();
        var procedureEnabledProcedureLevels = procedureEnabledLevels
            .Select(ladderPolicy.ProcedureLevel).ToHashSet();
        var linkedProcedureRevisionIds = procedureEnabledLevels.Count == 0
            ? []
            : await (from link in db.TestCaseProcedureLinks.AsNoTracking()
                     join member in db.BaselineTestProcedures.AsNoTracking()
                         on link.CaseRevisionId equals member.RevisionId
                     join caseArtifact in db.TestProcedures.AsNoTracking()
                         on member.ProcedureId equals caseArtifact.Id
                     where member.BaselineId == baselineId
                         && caseArtifact.ArtifactKind == VerificationArtifactKind.Case
                         && procedureEnabledProcedureLevels.Contains(caseArtifact.Level)
                     select link.ProcedureRevisionId).Distinct().ToListAsync(ct);
        var procedureRevisionIds = await (from coverage in db.TestCoverage.AsNoTracking()
                                          join member in db.BaselineRequirements.AsNoTracking() on coverage.RequirementRevisionId equals member.RevisionId
                                          join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                                          where member.BaselineId == baselineId && configuredRequirementLevels.Contains(artifact.Level)
                                          select coverage.ProcedureRevisionId).Distinct().ToListAsync(ct);
        // Union, never replace: linked Procedures join the coverage-derived set, and the executable-binding
        // filter below drops the now-non-executable Case revisions at Procedure-enabled levels.
        procedureRevisionIds = procedureRevisionIds.Concat(linkedProcedureRevisionIds).Distinct().ToList();
        var allowedProcedureLevels = ladderPolicy.Definitions.Where(x => x.Verification is not null)
            .Select(x => x.Verification!.ProcedureLevel).ToHashSet();
        return await (from revision in db.TestProcedureRevisions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.Id))
                      join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                      where procedure.ProjectId == projectId && allowedProcedureLevels.Contains(procedure.Level)
                          && executableBindings.Any(binding =>
                              binding.Level == procedure.Level
                              && binding.Kind == procedure.ArtifactKind)
                      orderby procedure.BaseNumber
                      select new ValueTuple<Guid, string>(revision.Id, procedure.BaseNumber + "." + revision.Revision.ToString("D2"))).ToListAsync(ct);
    }
}
