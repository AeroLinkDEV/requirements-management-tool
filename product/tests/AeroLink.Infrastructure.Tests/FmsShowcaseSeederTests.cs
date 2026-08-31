using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AeroLink.Infrastructure.Tests;

public sealed class FmsShowcaseSeederTests
{
    private static async Task<Guid[]> OwnedScenarioIdsAsync(AeroLinkDbContext db, Guid programId, string prefix)
    {
        var details = await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.StepKey.StartsWith(prefix))
            .OrderBy(x => x.StepKey).Select(x => x.Detail).ToListAsync();
        return details.Select(Guid.Parse).ToArray();
    }

    [Fact]
    public async Task Generates_exact_released_15_baseline_and_active_16_work_without_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
            await new IdentitySeeder(db).EnsureSeededAsync();
            var seeder = new FmsShowcaseSeeder(db); var first = await seeder.EnsureSeededAsync(); var second = await seeder.EnsureSeededAsync();
            Assert.Equal(150, first.SystemRequirements); Assert.Equal(400, first.HighLevelRequirements); Assert.Equal(700, first.LowLevelRequirements);
            Assert.Equal(30, first.HistoricalScrs); Assert.Equal(75, first.HistoricalSwcrs); Assert.Equal(1100, first.TraceLinks);
            Assert.Equal(515, first.TestProcedures); Assert.Equal(520, first.TestExecutions); Assert.Equal(6, first.Documents); Assert.Equal(first, second);
            var onlyProgram = Assert.Single(await db.Programs.AsNoTracking().ToListAsync());
            Assert.Equal(FmsShowcaseSeeder.ProgramCode, onlyProgram.Code);
            var project = Assert.Single(await db.Projects.AsNoTracking().Where(x => x.ProgramId == onlyProgram.Id).ToListAsync());
            var ladder = await db.ProjectLadderConfigurations
                .Include(x => x.Steps).Include(x => x.AllowedUpstream)
                .SingleAsync(x => x.ProjectId == project.Id);
            var resolvedLadder = ProjectLadderResolver.Resolve(ladder);
            // #726: the showcase starts from the new-project default — an authored NonDefault Draft with
            // software [Case, Procedure]. The seeder writes legacy-shaped demo content directly, so it does
            // not itself run the first-content seal authority.
            Assert.False(resolvedLadder.AgreesWithLegacyDefault());
            Assert.Equal(ProjectLadderConfigurationClassification.NonDefault, ladder.Classification);
            Assert.Equal(ProjectLadderConfigurationState.Draft, ladder.State);
            Assert.Equal(2, ladder.AllowedUpstream.Count);
            Assert.Equal(["1.5", "1.6"], await db.Releases.AsNoTracking().Where(x => x.ProjectId == project.Id).OrderBy(x => x.Version).Select(x => x.Version).ToArrayAsync());
            Assert.Equal(1250, await db.BaselineRequirements.CountAsync(x => x.BaselineId == first.ReleasedBaselineId));
            Assert.Equal(1250, await db.TestCoverage.Select(x => x.RequirementRevisionId).Distinct().CountAsync());
            Assert.Equal("SW-01.50", await db.CandidateBaselines.Where(x => x.Id == first.ReleasedBaselineId).Select(x => x.BaseNumber).SingleAsync());
            Assert.Equal("SW-01.60", await db.CandidateBaselines.Where(x => x.ReleaseId == first.ActiveReleaseId).Select(x => x.BaseNumber).SingleAsync());
            var historicalReviews = await db.TestChangeReviews.Where(x => x.ReleaseId != first.ActiveReleaseId).ToListAsync();
            Assert.Equal(105, historicalReviews.Count);
            Assert.All(historicalReviews, x => Assert.Equal(TestChangeReviewState.Approved, x.State));
            Assert.Equal(historicalReviews.Count, historicalReviews.Select(x => new { x.ChangeRequestId, x.Discipline }).Distinct().Count());
            Assert.True(await db.RequirementRevisions.GroupBy(x => x.ArtifactId).AllAsync(x => x.Count() >= 1));
            var active = db.SystemChangeRequests.Where(x => x.TargetReleaseId == first.ActiveReleaseId);
            Assert.Equal(16, await active.CountAsync()); Assert.Equal(3, await active.CountAsync(x => x.State == ChangeRequestState.SelectedForBaseline));
            Assert.Equal(2, await active.CountAsync(x => x.State == ChangeRequestState.Approved)); Assert.Equal(3, await active.CountAsync(x => x.State == ChangeRequestState.InReview)); Assert.Equal(5, await active.CountAsync(x => x.State == ChangeRequestState.Draft)); Assert.Equal(2, await active.CountAsync(x => x.State == ChangeRequestState.Deferred)); Assert.Equal(1, await active.CountAsync(x => x.State == ChangeRequestState.Withdrawn));
            var codeRecords = await db.CodeTraceabilityRecords.AsNoTracking().ToListAsync();
            Assert.Equal(9, codeRecords.Count);
            Assert.Equal(5, codeRecords.Count(x => x.ReleaseId != first.ActiveReleaseId));
            Assert.Equal(4, codeRecords.Count(x => x.ReleaseId == first.ActiveReleaseId));
            Assert.All(codeRecords, x => Assert.True(x.IsDemonstration));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Fresh_identity_ordering_freezes_real_sqa_identity_and_covers_scenario_lifecycles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-scenarios-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            // Exercise the normal upgrade path where the existing identity directory already contains the
            // seeded SQA account. The showcase seeder must freeze that real account ID into the package.
            await new IdentitySeeder(db).EnsureSeededAsync();
            var seeder = new FmsShowcaseSeeder(db);
            var summary = await seeder.EnsureSeededAsync();
            var release15Id = await db.Releases.Where(x => x.ProjectId == summary.ProjectId && x.Version == "1.5").Select(x => x.Id).SingleAsync();
            var interfaceScenarioIds = await OwnedScenarioIdsAsync(db, summary.ProgramId, "scenario-richness/interface/");
            var reportScenarioIds = await OwnedScenarioIdsAsync(db, summary.ProgramId, "scenario-richness/problem-report/");
            var firstInterface = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => interfaceScenarioIds.Contains(x.Id))
                .OrderBy(x => x.BaseNumber).Select(x => new { x.BaseNumber, x.Revision, x.State, x.AuthorId }).ToListAsync();
            var firstReports = await db.ProblemReports.AsNoTracking().Where(x => reportScenarioIds.Contains(x.Id))
                .OrderBy(x => x.ReportNumber).Select(x => new { x.Id, x.ReportNumber, x.Revision, x.State, x.ResponsibleEngineerId, x.TargetReleaseId, x.ResolutionVerificationExecutionId, x.ClosureApprovedAt, x.AdditionalInformation, x.CreatedAt }).ToListAsync();

            Assert.Equal(8, firstInterface.Count);
            Assert.Equal(8, firstReports.Count);
            Assert.Equal(16, await db.SystemChangeRequests.CountAsync(x => x.ProjectId == summary.ProjectId && x.TargetReleaseId == summary.ActiveReleaseId));
            Assert.Equal(6, firstReports.Count(x => x.TargetReleaseId == release15Id));
            Assert.Equal(2, firstReports.Count(x => x.TargetReleaseId == summary.ActiveReleaseId));
            var eligibleAuthors = new[] { "systems.author", "software.author" };
            var eligibleOwners = new[] { "systems.author", "software.author", "test.engineer", "engineer.demo", "test.author" };
            Assert.All(firstInterface, item => Assert.Contains(item.AuthorId, eligibleAuthors, StringComparer.OrdinalIgnoreCase));
            Assert.All(firstReports, item => Assert.Contains(item.ResponsibleEngineerId, eligibleOwners, StringComparer.OrdinalIgnoreCase));
            Assert.Contains(firstInterface, x => x.State == ChangeRequestState.SelectedForBaseline);
            Assert.Contains(firstInterface, x => x.State == ChangeRequestState.InReview);
            Assert.Contains(firstInterface, x => x.State == ChangeRequestState.Deferred);
            Assert.Contains(firstInterface, x => x.State == ChangeRequestState.Withdrawn);
            Assert.Contains(firstReports, x => x.State == ProblemReportState.Draft);
            Assert.Contains(firstReports, x => x.State == ProblemReportState.Implementing);
            Assert.Contains(firstReports, x => x.State == ProblemReportState.Verifying);
            Assert.Contains(firstReports, x => x.State == ProblemReportState.WaitingForSqaToClose);
            Assert.Contains(firstReports, x => x.State == ProblemReportState.Closed);
            Assert.Contains(firstReports, x => x.State == ProblemReportState.Rejected);
            Assert.Equal(5, firstReports.Select(x => x.ResponsibleEngineerId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(firstReports, x => Assert.True(x.TargetReleaseId == release15Id || x.TargetReleaseId == summary.ActiveReleaseId));

            var buildScopeLinks = await db.ProblemReportLinks.AsNoTracking()
                .Where(x => firstReports.Select(report => report.Id).Contains(x.ProblemReportId)
                    && x.ArtifactType == "Release" && x.Relationship == ProblemReportRelationshipPolicy.BuildScope)
                .ToListAsync();
            Assert.Equal(8, buildScopeLinks.Count);
            Assert.All(firstReports, report => Assert.Single(buildScopeLinks.Where(link => link.ProblemReportId == report.Id && link.ArtifactId == report.TargetReleaseId)));

            foreach (var index in new[] { 6, 7 })
            {
                var report = Assert.Single(firstReports.Where(x => x.Id == reportScenarioIds[index - 1]));
                Assert.NotNull(report.ResolutionVerificationExecutionId);
                Assert.True(await db.ProblemReportLinks.AnyAsync(x => x.ProblemReportId == report.Id
                    && x.ArtifactType == "TestExecution" && x.ArtifactId == report.ResolutionVerificationExecutionId
                    && x.Relationship == ProblemReportRelationshipPolicy.ResolutionVerification));
                Assert.True(await db.ProblemReportRevisions.AnyAsync(x => x.ProblemReportId == report.Id && x.EventType == "ResolutionVerified"));
                var candidate = await db.ProblemReportClosureCandidates.AsNoTracking()
                    .SingleAsync(x => x.ProblemReportId == report.Id);
                Assert.Equal(report.ResolutionVerificationExecutionId, candidate.VerificationExecutionId);
                Assert.Equal(index == 6 ? ProblemReportClosureCandidateState.Pending : ProblemReportClosureCandidateState.Approved, candidate.State);
                var execution = await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == report.ResolutionVerificationExecutionId);
                var failure = await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.RetestOfExecutionId);
                Assert.Equal(TestOutcome.Fail, failure.Outcome);
                Assert.Equal(TestOutcome.Pass, execution.Outcome);
                Assert.Equal(release15Id, failure.ReleaseId);
                Assert.Equal(release15Id, execution.ReleaseId);
                Assert.True(failure.ExecutedAt < report.CreatedAt);
                Assert.True(report.CreatedAt < execution.RecordedAt);
                var history = (await db.ProblemReportRevisions.AsNoTracking()
                    .Where(x => x.ProblemReportId == report.Id).ToListAsync())
                    .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToList();
                var expectedEvents = index == 7
                    ? new[] { "ProblemReportCreatedFromFailedExecution", "ReadyForSccb", "OpenedBySccb", "ImplementationStarted", "InvestigationRecorded", "ResolutionProposed", "ResolutionVerified", "ClosureApproved" }
                    : new[] { "ProblemReportCreatedFromFailedExecution", "ReadyForSccb", "OpenedBySccb", "ImplementationStarted", "InvestigationRecorded", "ResolutionProposed", "ResolutionVerified" };
                Assert.Equal(expectedEvents, history.Select(x => x.EventType).ToArray());
                var expectedActors = index == 7
                    ? new[] { report.ResponsibleEngineerId, report.ResponsibleEngineerId, "systems.reviewer", report.ResponsibleEngineerId,
                        report.ResponsibleEngineerId, report.ResponsibleEngineerId, "test.engineer", "quality.analyst" }
                    : new[] { report.ResponsibleEngineerId, report.ResponsibleEngineerId, "systems.reviewer", report.ResponsibleEngineerId,
                        report.ResponsibleEngineerId, report.ResponsibleEngineerId, "test.engineer" };
                Assert.Equal(expectedActors, history.Select(x => x.Actor).ToArray());
                Assert.Equal(new[] { "", "Draft", "ReadyForSccb", "Open", "Implementing", "Implementing", "Verifying", "WaitingForSqaToClose" }
                    .Take(expectedEvents.Length), history.Select(x => x.FromState).ToArray());
                Assert.Equal(new[] { "", "ReadyForSccb", "Open", "Implementing", "Implementing", "Verifying", "WaitingForSqaToClose", "Closed" }
                    .Take(expectedEvents.Length), history.Select(x => x.ToState).ToArray());
                Assert.All(history, item =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.Actor));
                    Assert.False(string.IsNullOrWhiteSpace(item.ActorDisplayName));
                    Assert.Equal(ProblemReportEvidenceContract.Hash(item.SnapshotJson), item.SnapshotHash);
                });
                if (index == 7)
                {
                    var sqaAccountId = await db.UserAccounts.Where(x => x.UserName == "quality.analyst").Select(x => x.Id).SingleAsync();
                    var sqaMembership = await db.ProgramMemberships.SingleAsync(x => x.UserId == sqaAccountId
                        && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.SoftwareQualityAnalyst && x.EndedAt == null);
                    Assert.NotNull(report.ClosureApprovedAt);
                    Assert.True(sqaMembership.GrantedAt <= report.ClosureApprovedAt.Value);
                    Assert.Equal(sqaAccountId, candidate.ApprovedByAccountId);
                    Assert.True(await db.ProblemReportRevisions.AnyAsync(x => x.ProblemReportId == report.Id && x.EventType == "ClosureApproved"));
                    Assert.Equal(ProblemReportEvidenceContract.Hash(candidate.ClosurePackageJson), candidate.ClosurePackageHash);
                    using var package = JsonDocument.Parse(candidate.ClosurePackageJson);
                    Assert.Equal("FrozenAtApproval", package.RootElement.GetProperty("provenance").GetString());
                    Assert.Equal(candidate.Id, package.RootElement.GetProperty("candidate").GetProperty("id").GetGuid());
                    Assert.Equal(expectedEvents, package.RootElement.GetProperty("history").EnumerateArray()
                        .Select(item => item.GetProperty("eventType").GetString()).ToArray());
                }
            }

            await seeder.EnsureSeededAsync();
            interfaceScenarioIds = await OwnedScenarioIdsAsync(db, summary.ProgramId, "scenario-richness/interface/");
            reportScenarioIds = await OwnedScenarioIdsAsync(db, summary.ProgramId, "scenario-richness/problem-report/");
            var secondInterface = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => interfaceScenarioIds.Contains(x.Id))
                .OrderBy(x => x.BaseNumber).Select(x => new { x.BaseNumber, x.Revision, x.State, x.AuthorId }).ToListAsync();
            var secondReports = await db.ProblemReports.AsNoTracking().Where(x => reportScenarioIds.Contains(x.Id))
                .OrderBy(x => x.ReportNumber).Select(x => new { x.Id, x.ReportNumber, x.Revision, x.State, x.ResponsibleEngineerId, x.TargetReleaseId, x.ResolutionVerificationExecutionId, x.ClosureApprovedAt, x.AdditionalInformation, x.CreatedAt }).ToListAsync();
            Assert.Equal(firstInterface, secondInterface);
            Assert.Equal(firstReports, secondReports);
            Assert.All(await seeder.CheckInvariantsAsync(summary.ProgramId), x => Assert.True(x.Holds, $"{x.Key}: {x.Detail}"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Ended_sqa_membership_is_preserved_and_reopened_closure_stays_in_work()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-ended-sqa-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await new IdentitySeeder(db).EnsureSeededAsync();
            var seeder = new FmsShowcaseSeeder(db);
            var summary = await seeder.EnsureSeededAsync();
            var sqaId = await db.UserAccounts.Where(x => x.UserName == "quality.analyst").Select(x => x.Id).SingleAsync();
            var membership = await db.ProgramMemberships.SingleAsync(x => x.UserId == sqaId && x.ProgramId == summary.ProgramId
                && x.Role == ProgramRole.SoftwareQualityAnalyst && x.EndedAt == null);
            var scenarioRowsBefore = await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == summary.ProgramId);
            var membershipRowsBefore = await db.ProgramMemberships.CountAsync(x => x.UserId == sqaId
                && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.SoftwareQualityAnalyst);
            var account = await db.UserAccounts.SingleAsync(x => x.Id == sqaId);
            account.Disable(membership.GrantedAt.AddMinutes(1));
            await db.SaveChangesAsync();
            var disabledAuthority = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
            Assert.False(disabledAuthority.Ready);
            Assert.Equal("quality_analyst_account_inactive", disabledAuthority.Code);
            account.Enable();
            await db.SaveChangesAsync();
            membership.End("admin", membership.GrantedAt.AddHours(1));
            var reportIds = await OwnedScenarioIdsAsync(db, summary.ProgramId, "scenario-richness/problem-report/");
            var report7Id = reportIds[6];
            var report7 = await db.ProblemReports.SingleAsync(x => x.Id == report7Id);
            report7.Reopen("quality.analyst", "Reopen the historical scenario to qualify ended-authority handling.", membership.GrantedAt.AddHours(2));
            await db.SaveChangesAsync();

            await seeder.EnsureSeededAsync();

            Assert.Equal(membershipRowsBefore, await db.ProgramMemberships.CountAsync(x => x.UserId == sqaId
                && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.SoftwareQualityAnalyst));
            Assert.Equal(scenarioRowsBefore, await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == summary.ProgramId));
            var endedAuthority = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
            Assert.False(endedAuthority.Ready);
            Assert.Equal("quality_analyst_membership_inactive", endedAuthority.Code);
            Assert.False(await db.ProgramMemberships.AnyAsync(x => x.UserId == sqaId && x.ProgramId == summary.ProgramId
                && x.Role == ProgramRole.SoftwareQualityAnalyst && x.EndedAt == null));
            report7 = await db.ProblemReports.AsNoTracking().SingleAsync(x => x.Id == report7Id);
            Assert.Equal(ProblemReportState.Verifying, report7.State);
            Assert.Null(report7.ResolutionVerificationExecutionId);
            var historicalCandidate = await db.ProblemReportClosureCandidates.AsNoTracking()
                .Where(x => x.ProblemReportId == report7Id).OrderByDescending(x => x.Sequence).FirstAsync();
            Assert.Equal(ProblemReportClosureCandidateState.Approved, historicalCandidate.State);
            Assert.Equal(1, await db.ProblemReportRevisions.CountAsync(x => x.ProblemReportId == report7Id
                && x.EventType == "ClosureApproved"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task New_interface_scenarios_require_current_authority_for_every_controlled_actor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-interface-authority-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await new IdentitySeeder(db).EnsureSeededAsync();
            var seeder = new FmsShowcaseSeeder(db);
            var summary = await seeder.EnsureSeededAsync();

            var leadId = await db.UserAccounts.Where(x => x.UserName == "lead.reviewer").Select(x => x.Id).SingleAsync();
            var leadMembership = await db.ProgramMemberships.SingleAsync(x => x.UserId == leadId
                && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.Reviewer && x.EndedAt == null);
            leadMembership.End("admin", leadMembership.GrantedAt.AddDays(1));
            var missing = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
                && x.StepKey == "scenario-richness/interface/01");
            db.ShowcaseUpgradeSteps.Remove(missing);
            db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
                && x.StepKey == "scenario-richness"));
            await db.SaveChangesAsync();
            var requestCount = await db.SystemChangeRequests.CountAsync(x => x.ProjectId == summary.ProjectId);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.UpgradeAsync(summary.ProgramId));
            Assert.Contains("lead.reviewer", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(requestCount, await db.SystemChangeRequests.CountAsync(x => x.ProjectId == summary.ProjectId));
            Assert.False(await db.ShowcaseUpgradeSteps.AnyAsync(x => x.ProgramId == summary.ProgramId
                && x.StepKey == "scenario-richness/interface/01"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Mapped_incomplete_problem_report_evidence_preflights_test_engineer_and_preserves_late_grant_chronology()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-pr-authority-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await new IdentitySeeder(db).EnsureSeededAsync();
            var seeder = new FmsShowcaseSeeder(db);
            var summary = await seeder.EnsureSeededAsync();

            // Keep the durable scenario mapping and its controlled artifact, but invalidate the current
            // closure candidate. This leaves a mapped Problem Report with incomplete controlled evidence
            // while retaining the existing valid verification successor. It is the upgrade shape that used
            // to bypass the actor preflight because the mapping itself was present.
            var reportIdText = await db.ShowcaseUpgradeSteps.AsNoTracking()
                .Where(x => x.ProgramId == summary.ProgramId && x.StepKey == "scenario-richness/problem-report/06")
                .Select(x => x.Detail).SingleAsync();
            var reportId = Guid.Parse(reportIdText);
            var candidateBefore = await db.ProblemReportClosureCandidates.SingleAsync(x => x.ProblemReportId == reportId
                && x.State == ProblemReportClosureCandidateState.Pending);
            candidateBefore.Invalidate("test.engineer", "Force a retryable incomplete-evidence scenario for qualification.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            var scenarioStep = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
                && x.StepKey == "scenario-richness");

            var testEngineer = await db.UserAccounts.SingleAsync(x => x.UserName == "test.engineer");
            var testMembership = await db.ProgramMemberships.SingleAsync(x => x.UserId == testEngineer.Id
                && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.TestEngineer && x.EndedAt == null);
            var revisionCountBeforeRefusal = await db.ProblemReportRevisions.CountAsync(x => x.ProblemReportId == reportId);

            testEngineer.Disable(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            var disabled = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
            Assert.False(disabled.Ready);
            Assert.Equal("showcase_actor_authority_unavailable", disabled.Code);
            await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.UpgradeAsync(summary.ProgramId));
            Assert.Equal(revisionCountBeforeRefusal,
                await db.ProblemReportRevisions.CountAsync(x => x.ProblemReportId == reportId));
            Assert.True(await db.ShowcaseUpgradeSteps.AnyAsync(x => x.Id == scenarioStep.Id));

            testEngineer.Enable();
            var endedAt = DateTimeOffset.UtcNow;
            testMembership.End("operator", endedAt);
            await db.SaveChangesAsync();
            var ended = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
            Assert.False(ended.Ready);
            Assert.Equal("showcase_actor_authority_unavailable", ended.Code);
            await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.UpgradeAsync(summary.ProgramId));
            Assert.Equal(revisionCountBeforeRefusal,
                await db.ProblemReportRevisions.CountAsync(x => x.ProblemReportId == reportId));

            // An explicit operator grant makes the actor current again. The new evidence must follow that
            // actual grant, even though the mapped test execution and Problem Report were authored in 2024.
            var lateGrant = endedAt.AddMinutes(1);
            db.ProgramMemberships.Add(new ProgramMembership(testEngineer.Id, summary.ProgramId,
                ProgramRole.TestEngineer, "operator", lateGrant));
            await db.SaveChangesAsync();
            var ready = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
            Assert.True(ready.Ready, ready.Detail);
            await seeder.UpgradeAsync(summary.ProgramId);

            var candidate = await db.ProblemReportClosureCandidates.AsNoTracking()
                .Where(x => x.ProblemReportId == reportId).OrderByDescending(x => x.Sequence).FirstAsync();
            Assert.Equal(ProblemReportClosureCandidateState.Pending, candidate.State);
            Assert.True(candidate.SelectedAt >= lateGrant,
                $"Closure candidate was selected at {candidate.SelectedAt:O} before the grant at {lateGrant:O}.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Late_actor_grant_covers_every_new_failed_execution_problem_report_revision()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-pr-new-authority-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await new IdentitySeeder(db).EnsureSeededAsync();
            var seeder = new FmsShowcaseSeeder(db);
            var summary = await seeder.EnsureSeededAsync();
            var scenario7Id = Guid.Parse(await db.ShowcaseUpgradeSteps.AsNoTracking()
                .Where(x => x.ProgramId == summary.ProgramId && x.StepKey == "scenario-richness/problem-report/07")
                .Select(x => x.Detail).SingleAsync());
            var scenario7VerificationId = await db.ProblemReports.AsNoTracking()
                .Where(x => x.Id == scenario7Id).Select(x => x.ResolutionVerificationExecutionId).SingleAsync();

            // Remove only the durable ownership pointer for scenario 06. The old controlled report remains
            // immutable history; the explicit upgrade must create a new owned scenario and attribute every
            // new action on the real authority timeline rather than the 2024 execution that motivated it.
            db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
                && x.StepKey == "scenario-richness/problem-report/06"));
            db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
                && x.StepKey == "scenario-richness"));

            var testEngineer = await db.UserAccounts.SingleAsync(x => x.UserName == "test.engineer");
            var priorMembership = await db.ProgramMemberships.SingleAsync(x => x.UserId == testEngineer.Id
                && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.TestEngineer && x.EndedAt == null);
            var endedAt = DateTimeOffset.UtcNow;
            priorMembership.End("operator", endedAt);
            var lateGrant = endedAt.AddMinutes(1);
            db.ProgramMemberships.Add(new ProgramMembership(testEngineer.Id, summary.ProgramId,
                ProgramRole.TestEngineer, "operator", lateGrant));
            await db.SaveChangesAsync();

            var ready = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
            Assert.True(ready.Ready, ready.Detail);
            await seeder.UpgradeAsync(summary.ProgramId);

            var newReportId = Guid.Parse(await db.ShowcaseUpgradeSteps.AsNoTracking()
                .Where(x => x.ProgramId == summary.ProgramId && x.StepKey == "scenario-richness/problem-report/06")
                .Select(x => x.Detail).SingleAsync());
            var revisions = (await db.ProblemReportRevisions.AsNoTracking()
                .Where(x => x.ProblemReportId == newReportId).ToListAsync()).OrderBy(x => x.OccurredAt).ToList();
            Assert.NotEmpty(revisions);
            Assert.All(revisions, revision => Assert.True(revision.OccurredAt >= lateGrant,
                $"{revision.EventType} by {revision.Actor} occurred at {revision.OccurredAt:O} before {lateGrant:O}."));

            var requiredRoles = new Dictionary<string, ProgramRole>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.engineer"] = ProgramRole.TestEngineer,
                ["systems.reviewer"] = ProgramRole.Reviewer,
                ["quality.analyst"] = ProgramRole.SoftwareQualityAnalyst,
            };
            var accounts = await db.UserAccounts.AsNoTracking()
                .Where(x => requiredRoles.Keys.Contains(x.UserName)).ToDictionaryAsync(x => x.UserName, StringComparer.OrdinalIgnoreCase);
            var memberships = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == summary.ProgramId && accounts.Values.Select(a => a.Id).Contains(x.UserId))
                .ToListAsync();
            foreach (var revision in revisions)
            {
                var account = accounts[revision.Actor];
                var role = requiredRoles[revision.Actor];
                Assert.Contains(memberships, membership => membership.UserId == account.Id && membership.Role == role
                    && membership.GrantedAt <= revision.OccurredAt
                    && (membership.EndedAt is null || membership.EndedAt.Value > revision.OccurredAt));
            }

            var report = await db.ProblemReports.AsNoTracking().SingleAsync(x => x.Id == newReportId);
            var verificationId = Assert.IsType<Guid>(report.ResolutionVerificationExecutionId);
            var verification = await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == verificationId);
            Assert.Equal(TestOutcome.Pass, verification.Outcome);
            Assert.True(verification.RecordedAt >= lateGrant);
            var predecessorId = Assert.IsType<Guid>(verification.RetestOfExecutionId);
            var predecessor = await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == predecessorId);
            Assert.Equal(TestOutcome.Pass, predecessor.Outcome);
            Assert.NotNull(predecessor.RetestOfExecutionId);
            Assert.True(predecessor.RecordedAt < lateGrant);
            Assert.Equal(predecessor.ProjectId, verification.ProjectId);
            Assert.Equal(predecessor.ReleaseId, verification.ReleaseId);
            Assert.Equal(predecessor.SoftwareBuildId, verification.SoftwareBuildId);
            Assert.Equal(predecessor.ProcedureRevisionId, verification.ProcedureRevisionId);
            var policyDecision = await new ProblemReportClosureVerificationPolicy(db)
                .ValidateAsync(report, verification, CancellationToken.None);
            Assert.True(policyDecision.Accepted, $"{policyDecision.Code} {policyDecision.Error}");
            Assert.Equal("test.engineer", verification.ExecutedBy);
            Assert.Single(await db.ShowcaseUpgradeSteps.AsNoTracking().Where(x => x.ProgramId == summary.ProgramId
                && x.StepKey == "scenario-richness/problem-report-verification/06").ToListAsync());

            var executionCount = await db.TestExecutions.CountAsync();
            Assert.Empty(await seeder.UpgradeAsync(summary.ProgramId));
            Assert.Equal(executionCount, await db.TestExecutions.CountAsync());
            Assert.Equal(scenario7VerificationId, await db.ProblemReports.AsNoTracking()
                .Where(x => x.Id == scenario7Id).Select(x => x.ResolutionVerificationExecutionId).SingleAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Existing_collidable_low_numbers_are_not_mistaken_for_owned_showcase_scenarios()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-collision-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
            await new IdentitySeeder(db).EnsureSeededAsync();
            var seeder = new FmsShowcaseSeeder(db);
            var summary = await seeder.EnsureSeededAsync();
            var foreignInterface = new SystemChangeRequest("ICDCR-00001", 0, summary.ProjectId, summary.ActiveReleaseId,
                "Operator-owned interface change", "Existing controlled content.", "Existing controlled analysis. [FMSLIVE showcase scenario: interface-01]",
                "Existing controlled disposition.", "engineer.demo", DateTimeOffset.UtcNow, ChangeRequestType.Interface);
            var foreignReport = new ProblemReport(summary.ProjectId, "PR-00001", "Operator-owned problem report",
                "Existing controlled problem.", "Existing controlled analysis.", "engineer.demo", DateTimeOffset.UtcNow,
                targetReleaseId: summary.ActiveReleaseId, responsibleEngineerId: "engineer.demo",
                additionalInformation: "Operator-owned content. [FMSLIVE showcase scenario: problem-report-01]",
                category: ProblemReportCategory.CodeFunctional);
            db.SystemChangeRequests.Add(foreignInterface); db.ProblemReports.Add(foreignReport); await db.SaveChangesAsync();

            // Force the enrichment retry boundary while leaving the foreign rows' copied display breadcrumbs
            // in place. Scenario ownership is durable-step based and the preferred high range is collision-safe,
            // so a user-authored marker cannot be selected for the missing owned scenario or receive its links.
            var missingInterfaceMapping = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
                && x.StepKey == "scenario-richness/interface/01");
            db.ShowcaseUpgradeSteps.Remove(missingInterfaceMapping);
            db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
                && x.StepKey == "scenario-richness"));
            await db.SaveChangesAsync();
            await seeder.UpgradeAsync(summary.ProgramId);
            var interfaceScenarioIds = await OwnedScenarioIdsAsync(db, summary.ProgramId, "scenario-richness/interface/");
            var reportScenarioIds = await OwnedScenarioIdsAsync(db, summary.ProgramId, "scenario-richness/problem-report/");
            var ownedInterfaces = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => interfaceScenarioIds.Contains(x.Id))
                .ToListAsync();
            var ownedReports = await db.ProblemReports.AsNoTracking().Where(x => reportScenarioIds.Contains(x.Id))
                .ToListAsync();
            Assert.Equal(8, ownedInterfaces.Count); Assert.Equal(8, ownedReports.Count);
            Assert.DoesNotContain(ownedInterfaces, x => x.Id == foreignInterface.Id);
            Assert.DoesNotContain(ownedReports, x => x.Id == foreignReport.Id);
            Assert.Contains(await db.SystemChangeRequests.AsNoTracking().Select(x => x.BaseNumber).ToListAsync(), x => x == "ICDCR-00001");
            Assert.Contains(await db.ProblemReports.AsNoTracking().Select(x => x.ReportNumber).ToListAsync(), x => x == "PR-00001");
            Assert.Equal("Existing controlled analysis. [FMSLIVE showcase scenario: interface-01]", foreignInterface.Analysis);
            Assert.Equal("Operator-owned content. [FMSLIVE showcase scenario: problem-report-01]", foreignReport.AdditionalInformation);
            Assert.Empty(await db.ProblemReportLinks.AsNoTracking().Where(x => x.ProblemReportId == foreignReport.Id).ToListAsync());
            Assert.All(ownedInterfaces, x => Assert.StartsWith("ICDCR-866", x.BaseNumber));
            Assert.All(ownedReports, x => Assert.StartsWith("PR-866", x.ReportNumber));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The showcase covered all 1,250 of its requirements, so it could never demonstrate the product finding
    /// a verification gap — the question the tool exists to answer. One FMS 1.6 rework item now puts an
    /// approved System procedure back into revision, which is enough to make the coverage it provides stop
    /// counting without disturbing a single released FMS 1.5 record.
    /// </summary>
    [Fact]
    public async Task An_in_work_procedure_revision_creates_suspect_coverage_that_reseeding_does_not_multiply()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-gap-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
            await new IdentitySeeder(db).EnsureSeededAsync();
            var seeder = new FmsShowcaseSeeder(db);
            var first = await seeder.EnsureSeededAsync();
            await seeder.EnsureSeededAsync();

            var procedure = await db.TestProcedures.AsNoTracking().SingleAsync(x => x.BaseNumber == "SYSTP-000040");
            var revisions = await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.ProcedureId == procedure.Id).OrderBy(x => x.Revision).ToListAsync();

            // Seeding twice must leave one in-work revision, not two.
            Assert.Equal([0, 1], revisions.Select(x => x.Revision).ToArray());
            Assert.Equal(TestProcedureState.Approved, revisions[0].State);
            Assert.Equal(TestProcedureState.Draft, revisions[1].State);

            // Released FMS 1.5 is untouched: every effective revision still carries its coverage link.
            Assert.Equal(1250, await db.TestCoverage.Select(x => x.RequirementRevisionId).Distinct().CountAsync());

            var effective = await db.BaselineRequirements.AsNoTracking()
                .Where(x => x.BaselineId == first.ReleasedBaselineId).Select(x => x.RevisionId).ToListAsync();
            var states = await VerificationCoverageProjection.StatesAsync(db, effective, default);
            var suspect = states.Where(x => x.Value == RequirementCoverageState.Suspect).Select(x => x.Key).OrderBy(x => x).ToArray();
            var carried = await db.TestCoverage.AsNoTracking()
                .Where(x => x.ProcedureRevisionId == revisions[0].Id).Select(x => x.RequirementRevisionId).ToListAsync();

            // Exactly the requirements that one procedure covers, and nothing else in the programme.
            Assert.Equal(carried.OrderBy(x => x).ToArray(), suspect);
            Assert.Equal(2, suspect.Length);

            // Uncovered is deliberately not seeded — see EnsureVerificationCoverageGapAsync for why.
            Assert.DoesNotContain(RequirementCoverageState.Uncovered, states.Values);
        }
        finally { File.Delete(path); }
    }
}
