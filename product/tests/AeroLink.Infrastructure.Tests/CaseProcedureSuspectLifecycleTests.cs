using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class CaseProcedureSuspectLifecycleTests
{
    [Fact]
    public async Task Materialized_case_successor_carries_a_new_suspect_link_and_preserves_released_history()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-case-procedure-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var policy = ProcedureEnabledPolicy();
            var program = new ProgramRecord("Case Procedure lifecycle", "CPL");
            var project = new ProjectRecord(program.Id, "Software", "Case Procedure lifecycle project");
            var release = new SoftwareRelease(project.Id, "7.27", false);
            var source = new SystemChangeRequest("HLRCR-727100", 0, project.Id, release.Id,
                "Modify exact Case", "Problem", "Analysis", "Solution", "case.author", now,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            var predecessorChange = ApprovedSystemChange(project.Id, release.Id, "SRCR-727100", now);
            var successorChange = ApprovedSystemChange(project.Id, release.Id, "SRCR-727101", now);
            var caseArtifact = new TestProcedure(project.Id, "HLRTC-727100", "Controlled Case",
                "case.author", now, TestProcedureLevel.HighLevel, policy, VerificationArtifactKind.Case);
            var caseRevision0 = new TestProcedureRevision(caseArtifact.Id, 0, "Case objective", "Setup",
                "Steps", "Expected", TestProcedureState.Approved, "case.author", now,
                parentKind: VerificationProcedureParentKind.Derived,
                derivedRationale: "The historical Case is standalone in this focused lifecycle fixture.");
            var procedureArtifact = new TestProcedure(project.Id, "HLRTP-727100", "Controlled Procedure",
                "procedure.author", now, TestProcedureLevel.HighLevel, policy,
                VerificationArtifactKind.Procedure, VerificationProcedureParentKind.Allocated);
            var procedureRevision0 = new TestProcedureRevision(procedureArtifact.Id, 0,
                "Procedure objective", "Procedure setup", "Procedure steps", "Expected observation",
                TestProcedureState.Draft, "procedure.author", now,
                environmentSetup: "Procedure setup", orderedSteps: "Procedure steps",
                testData: "Controlled test data", expectedObservations: "Expected observation",
                cleanup: "Restore the controlled fixture", toolingAutomation: "Qualified runner",
                parentKind: VerificationProcedureParentKind.Allocated);
            var historicalLink = new TestCaseProcedureLink(caseRevision0.Id, procedureRevision0.Id);

            var predecessor = new CandidateBaseline("BL-727100", 0, project.Id, release.Id, null,
                "Released predecessor", "cm", now);
            predecessor.Select(predecessorChange, "cm", now);
            predecessor.Freeze("cm", now);
            predecessor.MarkRequirementsMaterialized("cm", new string('a', 64), 0, now);
            predecessor.MarkTestProceduresMaterialized("cm", new string('b', 64), 1, now);
            predecessor.MarkReleased("cm", now);

            var caseKey = new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware,
                VerificationArtifactKind.Case);
            var caseReview = new TestChangeReview(project.Id, release.Id, source.Id, caseKey,
                source.DisplayNumber, now, "HLRTCCR-727100", authorId: "case.author");
            caseReview.RecordTestChangeRequired("case.author", now);
            caseReview.WriteCase("case.author", "Modify exact Case", "The Case changed.",
                "Its direct Procedure relationship must be reassessed.",
                "Materialize an exact Case successor.", now);
            caseReview.AddProcedureChange("case.author", new TestProcedureChangeDraft(
                caseArtifact.BaseNumber, 1, TestProcedureLevel.HighLevel, TestProcedureChangeKind.Modify,
                "Controlled Case, revised", "Revised Case objective", "Setup", "Revised steps",
                "Expected", "The Case behavior changed.", ParentKind: VerificationProcedureParentKind.Derived,
                DerivedRationale: "The revised Case remains standalone."), now, policy: policy);
            caseReview.SubmitForReview("case.author",
                [new ApproverSelection("case.reviewer", "Case Reviewer")], true, now);
            caseReview.ApproveActiveStage("case.reviewer", "The exact Case revision is approved.", now);

            var successor = new CandidateBaseline("BL-727101", 0, project.Id, release.Id, predecessor.Id,
                "In-work successor", "cm", now);
            successor.Select(successorChange, "cm", now);
            successor.SelectTestChangeRequest(caseReview, "cm", now);
            successor.Freeze("cm", now);
            successor.MarkRequirementsMaterialized("cm", new string('c', 64), 0, now);

            db.AddRange(program, project, release, caseArtifact, caseRevision0,
                procedureArtifact, procedureRevision0, historicalLink);
            using (db.UseSaveBoundaryPolicy(policy)) await db.SaveChangesAsync();
            db.Entry(procedureRevision0).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
            using (db.UseSaveBoundaryPolicy(policy)) await db.SaveChangesAsync();

            db.AddRange(source, predecessorChange, successorChange, predecessor, caseReview, successor,
                new BaselineTestProcedureSelection(predecessor.Id, caseArtifact.Id, caseRevision0.Id));
            using (db.UseSaveBoundaryPolicy(policy)) await db.SaveChangesAsync();

            await new TestProcedureBaselineMaterializer(db, policy)
                .MaterializeAsync(successor.Id, "cm", now.AddMinutes(1), default);

            var links = await db.TestCaseProcedureLinks.AsNoTracking()
                .OrderBy(x => x.CaseRevisionId).ToListAsync();
            Assert.Equal(2, links.Count);
            var preserved = links.Single(x => x.Id == historicalLink.Id);
            Assert.Equal(caseRevision0.Id, preserved.CaseRevisionId);
            Assert.Null(preserved.ExactLinkSuspectLifecycleId);
            var caseRevision1 = await db.TestProcedureRevisions.AsNoTracking()
                .SingleAsync(x => x.ProcedureId == caseArtifact.Id && x.Revision == 1);
            var carried = links.Single(x => x.Id != historicalLink.Id);
            Assert.Equal(caseRevision1.Id, carried.CaseRevisionId);
            Assert.Equal(procedureRevision0.Id, carried.ProcedureRevisionId);
            var lifecycle = await db.ExactLinkSuspectLifecycles.AsNoTracking()
                .SingleAsync(x => x.LinkKind == ExactLinkKind.CaseProcedure && x.LinkId == carried.Id);
            Assert.Equal(ExactLinkLifecycleState.Suspect, lifecycle.State);
            Assert.Equal(caseRevision1.Id, lifecycle.CauseVerificationRevisionId);
            var raised = await db.ExactLinkSuspectEvents.AsNoTracking().SingleAsync();
            Assert.Equal("cm", raised.ActorId);
            Assert.Equal(ExactLinkLifecycleEventType.Raised, raised.EventType);

            var campaign = new ReleaseCampaign(project.Id, release.Id, successor.Id,
                "In-work campaign", "cm", now.AddMinutes(2));
            db.ReleaseCampaigns.Add(campaign);
            await db.SaveChangesAsync();
            var blocked = await new ReleaseReadinessService(db, policy).CalculateAsync(campaign.Id, default);
            var coverageGate = blocked.Gates.Single(x => x.Code == "coverage");
            Assert.False(coverageGate.Complete);
            Assert.Contains("Case-to-Procedure", coverageGate.Detail, StringComparison.Ordinal);

            await new ExactLinkLifecycleService(db).AcknowledgeAsync(ExactLinkKind.CaseProcedure, carried.Id,
                "test.lead", "The Procedure impact is under review.", now.AddMinutes(3), default);
            await new ExactLinkLifecycleService(db).ResolveAsync(ExactLinkKind.CaseProcedure, carried.Id,
                ExactLinkResolutionOutcome.ExistingDownstreamRevisionRemainsValid, "test.lead",
                "The existing Procedure revision remains valid for the revised exact Case.",
                now.AddMinutes(4), default);
            var evidence = await db.ExactLinkSuspectEvents.AsNoTracking()
                .Where(x => x.LinkId == carried.Id).ToListAsync();
            evidence = evidence.OrderBy(x => x.OccurredAt).ToList();
            Assert.Equal([ExactLinkLifecycleEventType.Raised, ExactLinkLifecycleEventType.Acknowledged,
                ExactLinkLifecycleEventType.ResolutionRecorded], evidence.Select(x => x.EventType));
            Assert.Equal(["cm", "test.lead", "test.lead"], evidence.Select(x => x.ActorId));
            var discharged = await new ReleaseReadinessService(db, policy).CalculateAsync(campaign.Id, default);
            Assert.True(discharged.Gates.Single(x => x.Code == "coverage").Complete);

            var trackedHistorical = await db.TestCaseProcedureLinks.SingleAsync(x => x.Id == preserved.Id);
            db.Entry(trackedHistorical).Property(x => x.ExactLinkSuspectLifecycleId).CurrentValue = Guid.NewGuid();
            var retroactiveAttach = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Contains("immutable suspect lifecycle association", retroactiveAttach.Message, StringComparison.Ordinal);
            db.Entry(trackedHistorical).State = EntityState.Unchanged;

            var trackedCarried = await db.TestCaseProcedureLinks.SingleAsync(x => x.Id == carried.Id);
            db.Entry(trackedCarried).Property(x => x.ExactLinkSuspectLifecycleId).CurrentValue = Guid.NewGuid();
            var replace = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Contains("immutable suspect lifecycle association", replace.Message, StringComparison.Ordinal);
            db.Entry(trackedCarried).State = EntityState.Unchanged;

            db.Entry(trackedCarried).Property(x => x.ExactLinkSuspectLifecycleId).CurrentValue = null;
            var detach = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Contains("immutable suspect lifecycle association", detach.Message, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static ILadderPolicy ProcedureEnabledPolicy()
    {
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, now);
        var steps = new List<ProjectLadderStep>();
        foreach (var (level, position) in LegacyLadderPolicy.Instance.OrderedLevels.Select((x, i) => (x, i + 1)))
        {
            var kinds = level == RequirementLevel.System
                ? new[] { VerificationArtifactKind.Procedure }
                : new[] { VerificationArtifactKind.Case, VerificationArtifactKind.Procedure };
            var step = new ProjectLadderStep(configuration.Id, projectId, level, position,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities, now, kinds);
            configuration.Steps.Add(step); steps.Add(step);
        }
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[0].Id, steps[1].Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[1].Id, steps[2].Id, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }

    private static SystemChangeRequest ApprovedSystemChange(Guid projectId, Guid releaseId,
        string number, DateTimeOffset now)
    {
        var request = new SystemChangeRequest(number, 0, projectId, releaseId, "Baseline authority",
            "Problem", "Analysis", "Solution", "author", now);
        request.AddRequirementChange("author", $"SYSR-{number[^6..]}", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall retain the controlled fixture behavior.",
            "Baseline fixture authority.", "Analysis", now);
        request.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        request.ApproveActiveStage("reviewer", now);
        return request;
    }
}
