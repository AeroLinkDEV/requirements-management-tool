using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed partial class FmsShowcaseSeeder
{
    private const string ActiveVerificationPrefix = "active-verification-913/";

    /// <summary>
    /// Enriches an already materialized synthetic build with explicitly labelled demonstration results.
    /// No bench execution, binary identity or historical determination is asserted. Existing selections,
    /// results and immutable baseline membership remain intact. Fresh 1.6 retains its waiting lifecycle.
    /// </summary>
    private async Task<string?> EnsureActiveBuildVerificationAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var release = await db.Releases.SingleAsync(x => x.ProjectId == projectId && x.Version == "1.6", ct);
        if (release.IsReleased) throw new InvalidOperationException("Synthetic active-build enrichment cannot alter a released build.");
        var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseId == release.Id, ct);
        if (baseline?.RequirementsMaterializedAt is null)
            return "Build 1.6 is waiting for requirement materialization; no predecessor or execution population was substituted.";
        var manifest = await TestProcedureEffectivity.ForBaselineAsync(db, baseline.Id, ct);
        if (manifest is null || !manifest.IsExactManifest)
            throw new InvalidOperationException("Materialized Build 1.6 requires its own exact verification manifest before synthetic execution enrichment.");
        var policy = await resolver.ResolveAsync(projectId, ct);
        var levels = policy.OrderedLevels.Where(level => policy.Definition(level).VerificationProfile?.Enables(VerificationArtifactKind.Case) == true
            && policy.Definition(level).VerificationProfile?.Enables(VerificationArtifactKind.Procedure) == true)
            .Select(policy.ProcedureLevel).ToHashSet();
        var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseId == release.Id && x.BaselineId == baseline.Id, ct);
        if (campaign?.State is ReleaseCampaignState.InReview or ReleaseCampaignState.Released)
            throw new InvalidOperationException("A frozen release package cannot receive synthetic showcase results.");
        var obligations = await CaseProcedureSatisfaction.ForBaselineAsync(db, baseline.Id, release.Id, campaign?.SoftwareBuildId, levels, ct);
        if (obligations.Count == 0) return "This build has no configured Case-to-Procedure obligations; no executable family was invented.";
        if (obligations.Any(x => x.RequiredProcedureRevisionIds.Count == 0 || x.HasSuspectLink))
            throw new InvalidOperationException("Missing or suspect Case-to-Procedure links require their own disposition; synthetic results cannot repair them.");
        var caseIds = obligations.Select(x => x.CaseRevisionId).ToList();
        var cases = await (from revision in db.TestProcedureRevisions.AsNoTracking()
            join artifact in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals artifact.Id
            where caseIds.Contains(revision.Id)
            select new { revision.Id, artifact.BaseNumber, revision.Revision, artifact.Level }).ToListAsync(ct);
        var requiredIds = obligations.SelectMany(x => x.RequiredProcedureRevisionIds).Distinct().ToList();
        var procedures = await (from revision in db.TestProcedureRevisions.AsNoTracking()
            join artifact in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals artifact.Id
            where requiredIds.Contains(revision.Id)
            select new { revision.Id, artifact.BaseNumber, revision.Revision, revision.State, artifact.Level, artifact.ArtifactKind }).ToListAsync(ct);
        if (procedures.Count != requiredIds.Count || procedures.Any(x => x.State != TestProcedureState.Approved
            || x.ArtifactKind != VerificationArtifactKind.Procedure || !manifest.RevisionIds.Contains(x.Id)))
            throw new InvalidOperationException("Every demo execution must name an approved, exact effective Procedure revision.");
        var at = PersistedTimestamp(DateTimeOffset.UtcNow);
        const string actor = "test.engineer";
        await EnsureCurrentProgramAuthorityAsync(programId, actor, ProgramRole.TestEngineer, at, ct);
        const string planner = "program.manager";
        await EnsureCurrentProgramAuthorityAsync(programId, planner, ProgramRole.ProgramManager, at, ct);
        var sets = await db.BuildTestSets.Include(x => x.Entries).Where(x => x.ReleaseId == release.Id).ToListAsync(ct);
        foreach (var group in procedures.GroupBy(x => x.Level))
        {
            var discipline = group.Key == TestProcedureLevel.HighLevel ? TestChangeReviewDiscipline.HighLevelSoftware : TestChangeReviewDiscipline.LowLevelSoftware;
            var set = sets.SingleOrDefault(x => x.Discipline == discipline);
            if (set is null) { set = new BuildTestSet(projectId, release.Id, discipline, at); db.BuildTestSets.Add(set); }
            foreach (var procedure in group)
                set.Include(planner, procedure.Id, TestSelectionReason.Chosen, "Synthetic FMS 1.6 regression demonstration; exact effective Procedure selection.", at);
        }
        // The final quarter of each configured Case family is named waiting work. Keep any existing
        // result untouched; the diagnostic reads actual satisfaction and does not manufacture failure.
        var waiting = cases.GroupBy(x => x.Level).SelectMany(group => group.OrderBy(x => x.BaseNumber, StringComparer.Ordinal)
            .Skip((int)Math.Ceiling(group.Count() * .75))).Select(x => x.Id).ToHashSet();
        var waitingProcedureIds = obligations.Where(x => waiting.Contains(x.CaseRevisionId))
            .SelectMany(x => x.RequiredProcedureRevisionIds).ToHashSet();
        var latest = await ExecutionScope.LatestByProcedureAsync(db, requiredIds, release.Id, campaign?.SoftwareBuildId, ct);
        var newProcedures = procedures.Where(x => !waitingProcedureIds.Contains(x.Id) && !latest.ContainsKey(x.Id))
            .OrderBy(x => x.BaseNumber, StringComparer.Ordinal).ToList();
        EvidenceRecord? evidence = null;
        if (newProcedures.Count > 0)
        {
            if (evidenceStore is null)
                throw new InvalidOperationException("Materialized showcase qualification requires an explicitly configured evidence store; no persistent fallback is permitted.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Format = "aerolink-synthetic-verification-fixture/v1", IsDemonstration = true,
                Notice = "Owner-authorized synthetic FMS showcase data for #913. No external bench execution or binary qualification is asserted.",
                ProjectId = projectId, ReleaseId = release.Id, BaselineId = baseline.Id, campaign?.SoftwareBuildId,
                CreatedAt = at, Author = actor,
                Results = newProcedures.Select(x => new { ProcedureRevisionId = x.Id, Identifier = $"{x.BaseNumber}.{x.Revision:D2}", Outcome = "Pass", IsDemonstration = true })
            }, new JsonSerializerOptions { WriteIndented = true });
            using var stream = new MemoryStream(bytes);
            var stored = await evidenceStore.StoreAsync(stream, "fms-1.6-synthetic-verification-fixture.json", "application/json", ct);
            evidence = new EvidenceRecord(projectId, stored.OriginalFileName, stored.ContentType, stored.Size,
                stored.Sha256, stored.StorageKey, actor, at);
            db.EvidenceRecords.Add(evidence);
            foreach (var procedure in newProcedures)
            {
                var execution = new TestExecution(projectId, procedure.Id, campaign?.SoftwareBuildId, null,
                    TestOutcome.Pass, actor, "SYNTHETIC FMS 1.6 demonstration; no bench or binary qualification",
                    "Synthetic demonstration of a passing result for this exact Procedure revision; no external execution is asserted.",
                    evidence.Id.ToString("D"), at, at, release.Id);
                db.TestExecutions.Add(execution);
                db.TestExecutionEvidence.Add(new TestExecutionEvidence(execution.Id, evidence.Id));
                db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(programId, ActiveVerificationPrefix + "result/" + procedure.Id.ToString("D"), execution.Id.ToString("D"), at));
            }
        }
        foreach (var item in cases)
        {
            var key = ActiveVerificationPrefix + (waiting.Contains(item.Id) ? "waiting/" : "expected/") + item.Id.ToString("D");
            if (!await db.ShowcaseUpgradeSteps.AnyAsync(x => x.ProgramId == programId && x.StepKey == key, ct))
                db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(programId, key, $"{item.BaseNumber}.{item.Revision:D2}", at));
        }
        await db.SaveChangesAsync(ct);
        return $"Retained exact baseline {baseline.Id:D}; selected {procedures.Count} effective software Procedures; added {newProcedures.Count} explicitly synthetic, evidence-linked results. {waiting.Count} exact Cases are named waiting-work examples. Existing results and released history were preserved.";
    }
}
