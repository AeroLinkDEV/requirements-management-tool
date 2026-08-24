using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Traceability;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ReadinessGate(
    string Code,
    string Name,
    bool Complete,
    int Completed,
    int Total,
    string Detail,
    string Action,
    string EvaluationState = "Evaluated",
    string? PrerequisiteCode = null);
public sealed record ReleaseReadiness(int Percent, bool ReadyForRelease, IReadOnlyList<ReadinessGate> Gates);

public sealed class ReleaseReadinessService(AeroLinkDbContext db, ILadderPolicy? policy = null,
    IProjectLadderPolicyResolver? policyResolver = null)
{
    private readonly ILadderPolicy fallbackPolicy = policy ?? LegacyLadderPolicy.Instance;
    public async Task<ReleaseReadiness> CalculateAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await db.ReleaseCampaigns.AsNoTracking().Include(x => x.Approvals).SingleAsync(x => x.Id == campaignId, ct);
        var ladderPolicy = policyResolver is null
            ? fallbackPolicy
            : await policyResolver.ResolveAsync(campaign.ProjectId, ct);
        var configuredLevels = ladderPolicy.OrderedLevels.ToArray();
        var configuredDisciplines = configuredLevels
            .Where(level => ladderPolicy.Definition(level).Verification is not null)
            .Select(ladderPolicy.Discipline).ToHashSet();
        var configuredProcedureLevels = configuredLevels
            .Where(level => ladderPolicy.Definition(level).Verification is not null)
            .Select(ladderPolicy.ProcedureLevel).ToHashSet();
        var configuredVerificationRequirementLevels = configuredLevels
            .Where(level => ladderPolicy.Definition(level).Verification is not null).ToHashSet();
        var configuredChangeControlLevels = configuredLevels
            .Where(level => ladderPolicy.Definition(level).Has(LevelCapabilities.HasChangeControl)).ToHashSet();
        var changeControlConfigured = configuredChangeControlLevels.Count > 0;
        var systemChangeConfigured = ladderPolicy.IsChangeRequestScopeValid(ChangeRequestType.System, null);
        var interfaceChangeConfigured = ladderPolicy.IsChangeRequestScopeValid(ChangeRequestType.Interface, null);
        var softwareChangeLevels = configuredLevels
            .Where(level => ladderPolicy.IsChangeRequestScopeValid(ChangeRequestType.Software, level))
            .ToArray();
        var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == campaign.BaselineId, ct);
        var requests = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => x.TargetReleaseId == campaign.ReleaseId && x.State != ChangeRequestState.Deferred
                && (x.Type == ChangeRequestType.System
                    ? systemChangeConfigured
                    : x.Type == ChangeRequestType.Interface
                        ? interfaceChangeConfigured
                        : x.SoftwareLevel != null && softwareChangeLevels.Contains(x.SoftwareLevel.Value)))
            .ToListAsync(ct);
        var eligibleRequestIds = requests.Select(x => x.Id).ToHashSet();
        var impacts = await db.ImpactDispositions.AsNoTracking()
            .Where(x => x.CampaignId == campaignId && eligibleRequestIds.Contains(x.ChangeRequestId)).ToListAsync(ct);
        var members = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id)
                             join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                             where configuredLevels.Contains(artifact.Level)
                             select new { member.RevisionId, member.ArtifactId, Level = artifact.Level }).ToListAsync(ct);
        var coverageMembers = members.Where(x => configuredVerificationRequirementLevels.Contains(x.Level)).ToList();
        var revisionIds = members.Select(x => x.RevisionId).ToList();
        var coverageRevisionIds = coverageMembers.Select(x => x.RevisionId).ToList();
        var derivedLevels = ladderPolicy.OrderedLevels.Where(level => ladderPolicy.ParentLevels(level).Count > 0).ToArray();
        var derivedIds = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id) join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id where derivedLevels.Contains(artifact.Level) select member.RevisionId).ToListAsync(ct);
        var tracedDerivedIds = await db.RequirementTraces.AsNoTracking().Where(x => derivedIds.Contains(x.SourceRevisionId) && revisionIds.Contains(x.TargetRevisionId)).Select(x => x.SourceRevisionId).Distinct().ToListAsync(ct);
        var suspectTraceCount = await (from link in db.RequirementTraces.AsNoTracking()
                                       join lifecycle in db.ExactLinkSuspectLifecycles.AsNoTracking()
                                           on new { LinkKind = ExactLinkKind.RequirementTrace, LinkId = link.Id }
                                           equals new { lifecycle.LinkKind, LinkId = lifecycle.LinkId }
                                       where link.ProjectId == campaign.ProjectId
                                           && revisionIds.Contains(link.SourceRevisionId)
                                           && revisionIds.Contains(link.TargetRevisionId)
                                           && lifecycle.State != ExactLinkLifecycleState.Closed
                                       select link.Id).Distinct().CountAsync(ct);
        var procedureEnabledLevels = configuredLevels.Where(level =>
                ladderPolicy.Definition(level).VerificationProfile?.Enables(
                    VerificationArtifactKind.Procedure) == true
                && ladderPolicy.Definition(level).VerificationProfile?.Enables(
                    VerificationArtifactKind.Case) == true)
            .ToHashSet();
        var procedureEnabledProcedureLevels = procedureEnabledLevels.Select(ladderPolicy.ProcedureLevel)
            .ToHashSet();
        // Coverage counts only when it is settled, which takes three things.
        //
        // It must not be suspect: a link carried across a requirement change that nobody has reconfirmed
        // would otherwise let a requirement reach release on a procedure written against its previous wording.
        //
        // The procedure revision it names must itself be Approved. Nothing checked this before, so a
        // requirement could be counted as covered by a procedure still in draft.
        //
        // And the procedure must have no revision in flight. A procedure being modified has to be reviewed
        // and approved before anything relying on it can be considered approved; counting the superseded
        // revision in the meantime would claim a settled answer while the answer is being rewritten.
        // The predicate itself lives in VerificationCoverageProjection so the requirements workspace filter
        // reads the same definition. Two implementations of "covered" is how a workspace comes to disagree
        // with the gate it is meant to be preparing for.
        var procedureEffectivity = await TestProcedureEffectivity.ForBaselineAsync(db, baseline.Id, ct);
        IReadOnlyCollection<Guid>? effectiveProcedureRevisionIds = null;
        if (procedureEffectivity is not null)
        {
            // A retained procedure from an absent level must not satisfy current coverage merely because its
            // revision still appears in the historical baseline manifest. Intersect the manifest with both
            // the current project's procedures and the effective verification bindings before evaluating any
            // settled link.
            //
            // #726: with the software Procedure tier enabled, the exact baseline manifest holds Procedure
            // rows (the cutover rebound them) while Requirement coverage remains on Cases. The effectivity
            // set for coverage therefore keeps System Procedure manifest rows and recovers the effective
            // software Case population through the typed membership contract — never by searching for a
            // Case baseline row the migration intentionally removed.
            var systemProcedureRevisionIds = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                                    join procedure in db.TestProcedures.AsNoTracking()
                                                        on revision.ProcedureId equals procedure.Id
                                                    where procedure.ProjectId == campaign.ProjectId
                                                        && procedureEffectivity.RevisionIds.Contains(revision.Id)
                                                        && procedure.Level == TestProcedureLevel.System
                                                    select revision.Id).ToListAsync(ct);
            IReadOnlyList<Guid> softwareCaseRevisionIds;
            if (procedureEnabledLevels.Count == 0)
            {
                softwareCaseRevisionIds = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                                 join procedure in db.TestProcedures.AsNoTracking()
                                                     on revision.ProcedureId equals procedure.Id
                                                 where procedure.ProjectId == campaign.ProjectId
                                                     && procedureEffectivity.RevisionIds.Contains(revision.Id)
                                                     && procedure.ArtifactKind == VerificationArtifactKind.Case
                                                     && configuredProcedureLevels.Contains(procedure.Level)
                                                 select revision.Id).ToListAsync(ct);
            }
            else
            {
                var selections = await BaselineExecutableMembership.ForBaselineAsync(db, baseline.Id, ct);
                var sourceCases = await BaselineExecutableMembership.SourceCaseRevisionsAsync(db, selections, ct);
                softwareCaseRevisionIds = await BaselineExecutableMembership.EffectiveCaseRevisionIdsAsync(
                    db, selections, sourceCases, baseline.Id, procedureEnabledProcedureLevels, ct);
            }
            effectiveProcedureRevisionIds = systemProcedureRevisionIds
                .Concat(softwareCaseRevisionIds).Distinct().ToList();
        }
        var coveredIds = await VerificationCoverageProjection.SettledCoveredAsync(db, coverageRevisionIds, ct,
            effectiveProcedureRevisionIds, buildScoped: false);
        var docs = await db.ControlledDocuments.AsNoTracking().Where(x => x.BaselineId == baseline.Id).ToListAsync(ct);
        var configuredDocumentTypes = ladderPolicy.ControlledDocumentTypes.ToHashSet();
        var configuredDocs = docs.Where(x => configuredDocumentTypes.Contains(x.Type)).ToList();
        // A release cannot be declared ready while an unwaived controlled problem report remains a blocker.
        // This is deliberately project-scoped until product-line configuration provides exact release applicability.
        var allProblemBlockers = await db.ProblemReports.AsNoTracking()
            .Where(x => x.ProjectId == campaign.ProjectId && x.IsReleaseBlocker).ToListAsync(ct);
        var problemWaivers = await db.ReadinessWaivers.AsNoTracking().Where(x => x.ProjectId == campaign.ProjectId
            && x.BlockerType == "ProblemReportReleaseBlocker").ToListAsync(ct);
        var waiverDecisionAt = DateTimeOffset.UtcNow;
        var problemBlockers = allProblemBlockers.Where(report =>
            !problemWaivers.Any(waiver => waiver.IsActiveFor(report, waiverDecisionAt))).ToList();
        // Every requirement this release introduced or modified raised a verification impact item when its
        // change request was approved. Each one carries an owed decision: a procedure that covers it, or a
        // recorded confirmation that no test is required. A release with no requirement changes raises none,
        // and is complete by having nothing to decide.
        var verificationImpacts = await db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.ReleaseId == campaign.ReleaseId && eligibleRequestIds.Contains(x.ChangeRequestId)).ToListAsync(ct);
        var currentImpacts = verificationImpacts.Where(x => x.State != VerificationImpactState.Superseded).ToList();
        var impactDecided = currentImpacts.Count(x => x.State == VerificationImpactState.Resolved);
        var undecided = currentImpacts.Where(x => x.State != VerificationImpactState.Resolved).ToList();
        var testChangeReviews = await db.TestChangeReviews.AsNoTracking()
            .Where(x => x.ReleaseId == campaign.ReleaseId && configuredDisciplines.Contains(x.Discipline)).ToListAsync(ct);
        var approvedTestChangeReviews = testChangeReviews.Count(x => x.State == TestChangeReviewState.Approved);
        // What this build was planned to run, and whether it has run it.
        //
        // Loaded separately from the coverage-driven executions above, because a test set is not limited to
        // procedures that cover a changed requirement: exercising an area the change makes worth re-testing
        // is the other half of why a procedure is selected, and those procedures would be invisible here.
        var selectedRevisionIds = await db.BuildTestSetEntries.AsNoTracking()
            .Where(x => db.BuildTestSets.Any(set => set.Id == x.BuildTestSetId
                && set.ReleaseId == campaign.ReleaseId && configuredDisciplines.Contains(set.Discipline)))
            .Select(x => x.ProcedureRevisionId).Distinct().ToListAsync(ct);
        // #726: the selected set is the set of EFFECTIVE EXECUTABLE artifacts. With the software Procedure
        // tier enabled that means Procedure revisions, not the Case revisions beneath them; a Case-only
        // profile keeps Case revisions; System keeps its Procedure. One resolver, no per-consumer guesses.
        if (selectedRevisionIds.Count != 0)
            selectedRevisionIds = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                         join procedure in db.TestProcedures.AsNoTracking()
                                             .Where(EffectiveExecutableArtifact.ExecutablePredicate(ladderPolicy))
                                             on revision.ProcedureId equals procedure.Id
                                         where selectedRevisionIds.Contains(revision.Id)
                                             && configuredProcedureLevels.Contains(procedure.Level)
                                         select revision.Id).ToListAsync(ct);
        // Scoped through the one shared rule, not a local predicate.
        //
        // The previous condition relaxed to "any execution at all" whenever the campaign had no software
        // build, because `campaign.SoftwareBuildId == null || ...` is simply true for every row in that case
        // and nothing constrained the release. A determination recorded against released Build 1.5 could
        // therefore satisfy Build 1.6's verification and evidence gates. ExecutionScope is now the single
        // authority, shared with the Test Results workspace so the gate and the page it prepares cannot
        // disagree about which runs belong to the build.
        var selectedLatest = (await ExecutionScope.LatestByProcedureAsync(
            db, selectedRevisionIds, campaign.ReleaseId, campaign.SoftwareBuildId, ct)).Values.ToList();
        var selectedPassed = selectedLatest.Count(x => x.Outcome == TestOutcome.Pass);
        var selectedRunIds = selectedLatest.Select(x => x.Id).ToList();
        var selectedEvidenced = selectedRunIds.Count == 0 ? 0 : await db.TestExecutionEvidence.AsNoTracking()
            .Where(x => selectedRunIds.Contains(x.TestExecutionId)).Select(x => x.TestExecutionId).Distinct().CountAsync(ct);
        // #726: every effective exact software Case revision in a Procedure-enabled baseline must satisfy
        // ALL of its required exact Procedure links (effective in this baseline, selected in the matching
        // discipline BuildTestSet, latest build-scoped execution Pass, no suspect link). Zero links is
        // unsatisfied. This is the authoritative projection shared with reconciliation, never a global
        // "all selected tests passed" count.
        var caseObligations = await CaseProcedureSatisfaction.ForBaselineAsync(
            db, baseline.Id, campaign.ReleaseId, campaign.SoftwareBuildId,
            procedureEnabledProcedureLevels, ct);
        var unsatisfiedCaseProcedureCount = caseObligations.Count(x => !x.Satisfied);
        var unsatisfiedCaseProcedureDetail = string.Join(", ",
            caseObligations.Where(x => !x.Satisfied).Take(3)
                .Select(x => $"{x.RequiredProcedureRevisionIds.Count} required / {x.SatisfiedProcedureRevisionIds.Count} satisfied"));
        // An empty set is only an answer when there was nothing to plan. A build that changed something and
        // has selected nothing has not been planned yet, and a gate that passed it would be reporting
        // "nothing left to run" about a decision nobody has made.
        var nothingToTest = configuredDisciplines.Count == 0 || testChangeReviews.Count == 0;

        IReadOnlyList<RequiredCodeTraceabilityRequirement> requiredCode = baseline.RequirementsMaterializedAt is null
            ? Array.Empty<RequiredCodeTraceabilityRequirement>()
            : await CodeTraceabilityProjection.RequiredAsync(db, campaign.ProjectId, campaign.ReleaseId, baseline.Id, ladderPolicy, ct);
        var requiredCodeRevisionIds = requiredCode.Select(x => x.RevisionId).ToList();
        var mappedCode = requiredCodeRevisionIds.Count == 0 ? 0 : await db.CodeTraceabilityRecords.AsNoTracking()
            .Where(x => x.ProjectId == campaign.ProjectId && x.ReleaseId == campaign.ReleaseId
                && requiredCodeRevisionIds.Contains(x.RequirementRevisionId))
            .Select(x => x.RequirementRevisionId).Distinct().CountAsync(ct);

        var integrated = requests.Count(x => x.State == ChangeRequestState.SelectedForBaseline); var disposed = impacts.Count(x => x.State != ImpactDispositionState.Pending);
        var baselineMaterialized = baseline.RequirementsMaterializedAt is not null;
        var gates = new List<ReadinessGate>
        {
            new("change_control","Change requests integrated",!changeControlConfigured || (requests.Count > 0 && integrated == requests.Count),integrated,requests.Count,$"{requests.Count-integrated} non-deferred change request records remain outside the candidate baseline.","Approve and select every included change, or formally defer it."),
            new("impact_disposition","Impact analysis dispositioned",!changeControlConfigured || (impacts.Count > 0 && disposed == impacts.Count),disposed,impacts.Count,$"{impacts.Count-disposed} impact findings remain pending.","Disposition requirement, trace, verification, and document impacts."),
            new("baseline","Requirement baseline materialized",baseline.State is CandidateBaselineState.Frozen or CandidateBaselineState.Released && baseline.RequirementsMaterializedAt is not null,baseline.RequirementsMaterializedAt is null?0:1,1,"The release needs an exact frozen and materialized requirement set.","Freeze the candidate and materialize its requirements."),
            new("verification_impact","Verification impact decided",impactDecided == verificationImpacts.Count,impactDecided,verificationImpacts.Count,undecided.Count==0?"Every new, modified, and orphaned requirement in this release has a recorded verification decision.":$"{undecided.Count} changed requirement(s) await a verification decision: {string.Join(", ",undecided.Take(3).Select(x=>x.SubjectDisplayNumber))}.","Assign each item to a test engineer, then record an approved verification artifact or a confirmation that no test is required."),
            new("test_change_reviews","Test change requests approved",
                configuredDisciplines.Count == 0 || (testChangeReviews.Count > 0 && approvedTestChangeReviews == testChangeReviews.Count),
                approvedTestChangeReviews,testChangeReviews.Count,
                configuredDisciplines.Count == 0
                    ? "The effective ladder declares no verification disciplines, so no test change requests are owed."
                    : testChangeReviews.Count == 0
                    ? "No controlled test change requests have been raised for this software build."
                    : $"{testChangeReviews.Count-approvedTestChangeReviews} System, HLR, or LLR test change request(s) still require approval.",
                "Complete every verification artifact decision, submit each discipline review, and record test-lead approval."),
            baselineMaterialized
                ? new("traceability","Trace network complete",members.Count > 0 && tracedDerivedIds.Count == derivedIds.Count && suspectTraceCount == 0,tracedDerivedIds.Count,derivedIds.Count + suspectTraceCount,
                    members.Count == 0
                        ? "The materialized baseline contains no effective requirement revisions, so traceability cannot pass."
                        : suspectTraceCount == 0 ? "Every derived HLR/LLR must retain an exact parent link."
                            : $"{suspectTraceCount} exact trace link(s) are suspect, acknowledged, or still require downstream change.",
                    members.Count == 0
                        ? "Inspect the selected changes and materialized manifest; a releasable baseline must contain an effective requirement population."
                        : "Resolve orphan and suspect trace links.")
                : WaitingForMaterializedBaseline("traceability", "Trace network complete"),
            baselineMaterialized
                ? new("coverage","Requirement coverage complete",(coverageMembers.Count == 0 || coveredIds.Count == coverageMembers.Count) && unsatisfiedCaseProcedureCount == 0,coveredIds.Count,coverageMembers.Count + unsatisfiedCaseProcedureCount,
                    coverageMembers.Count == 0
                        ? (unsatisfiedCaseProcedureCount == 0
                            ? "The effective ladder declares no verification-capable requirement levels, so no coverage is owed."
                            : $"{unsatisfiedCaseProcedureCount} exact Case-to-Procedure obligation(s) remain unsatisfied: zero links, suspect links, missing effectivity/selection, or no latest build-scoped Pass ({unsatisfiedCaseProcedureDetail}).")
                        : $"{coverageMembers.Count-coveredIds.Count} effective verification requirement revisions have no settled coverage; {unsatisfiedCaseProcedureCount} exact Case-to-Procedure obligation(s) remain open: zero links, suspect links, missing effectivity/selection, or no latest build-scoped Pass ({unsatisfiedCaseProcedureDetail}).",
                    coverageMembers.Count == 0
                        ? (unsatisfiedCaseProcedureCount == 0
                            ? "No action is required: coverage is not applicable to the configured requirement levels."
                            : "Link every effective Case to an approved allocated Procedure, select it in the matching build test set, and record a latest build-scoped Pass.")
                        : "Approve every verification artifact being changed, confirm the coverage each changed requirement needs, link every effective Case to an approved allocated Procedure, select it in the matching build test set, and record a latest build-scoped Pass.")
                : WaitingForMaterializedBaseline("coverage", "Requirement coverage complete"),
            baselineMaterialized
                ? new("code_traceability", "Code traceability complete", mappedCode == requiredCode.Count, mappedCode, requiredCode.Count,
                    requiredCode.Count == 0
                        ? "No LLR revision changed in this build, so no implementation mapping is owed."
                        : $"{requiredCode.Count-mappedCode} exact LLR revision(s) lack a GitLab merge mapping or an attributable no-code decision.",
                    "Record immutable GitLab merge evidence or a justified no-code decision for every required exact LLR revision.")
                : WaitingForMaterializedBaseline("code_traceability", "Code traceability complete"),
            // The gate codes stay as they were. They are what the decision room looks its blockers up by, and
            // a build is rarely worth its whole suite whichever way the set of procedures was arrived at.
            baselineMaterialized
                ? new("verification","Selected test set has results",
                    selectedRevisionIds.Count == 0 ? nothingToTest : selectedPassed == selectedRevisionIds.Count,
                    selectedPassed,selectedRevisionIds.Count,
                    selectedRevisionIds.Count == 0
                        ? (nothingToTest
                            ? "This build changed nothing that needs testing, so no verification artifacts were selected."
                            : "No verification artifacts have been selected for this build yet.")
                        : $"{selectedRevisionIds.Count-selectedPassed} verification artifact(s) in the selected test set lack a latest Pass.",
                    selectedRevisionIds.Count == 0
                        ? "Choose the verification artifacts this build must run — those covering what changed, and any area worth re-exercising."
                        : "Record a determination for every verification artifact in the set. Testing beyond it continues after release.")
                : WaitingForMaterializedBaseline("verification", "Selected test set has results"),
            baselineMaterialized
                ? new("evidence","Selected test set results carry evidence",
                    selectedRevisionIds.Count == 0 ? nothingToTest : selectedEvidenced == selectedRevisionIds.Count,
                    selectedEvidenced,selectedRevisionIds.Count,
                    selectedRevisionIds.Count == 0
                        ? (nothingToTest
                            ? "This build changed nothing that needs testing, so no evidence is owed."
                            : "No verification artifacts have been selected for this build yet.")
                        : $"{selectedRevisionIds.Count-selectedEvidenced} result(s) in the selected test set lack checksummed evidence.",
                    "Attach the evidence package for every result in the selected test set.")
                : WaitingForMaterializedBaseline("evidence", "Selected test set results carry evidence"),
            new("problem_reports","Problem-report blockers resolved",problemBlockers.Count==0,0,problemBlockers.Count,problemBlockers.Count==0?"No unwaived controlled problem reports block this release.":$"{problemBlockers.Count} unwaived problem report blocker(s) remain: {string.Join(", ",problemBlockers.Take(3).Select(x=>x.DisplayNumber))}.","Resolve, formally disposition, or record an attributable waiver for every release-blocking problem report."),
            new("documents","Controlled outputs generated",
                configuredDocs.Select(x=>x.Type).Distinct().Count() == configuredDocumentTypes.Count
                && configuredDocumentTypes.All(type => configuredDocs.Any(x => x.Type == type)),
                configuredDocs.Select(x=>x.Type).Distinct().Count(), configuredDocumentTypes.Count,
                $"The release package requires exactly {ladderPolicy.ControlledDocumentTypes.Count} configured controlled document type(s).",
                "Generate every controlled document declared by the effective project ladder."),
            new("release_approval","Release approval complete",campaign.Approvals.Count>0 && campaign.Approvals.All(x=>x.State==ReleaseApprovalState.Approved),campaign.Approvals.Count(x=>x.State==ReleaseApprovalState.Approved),campaign.Approvals.Count==0?3:campaign.Approvals.Count,"Ordered release approval must be unanimous.","Start release review and collect every approval.")
        };
        var percent = (int)Math.Round(gates.Average(x => x.Total == 0 ? (x.Complete ? 100 : 0) : Math.Min(100, x.Completed * 100d / x.Total)));
        return new(percent, gates.All(x => x.Complete), gates);
    }

    private static ReadinessGate WaitingForMaterializedBaseline(string code, string name) =>
        new(code, name, false, 0, 0,
            "Waiting for a materialized baseline. The exact requirement-revision population does not exist yet, so this gate has not been evaluated.",
            "Complete the Requirement baseline materialized gate first: freeze the candidate baseline and materialize its requirements.",
            "WaitingForPrerequisite", "baseline");
}
