using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Qualifies the <see cref="FmsShowcaseSeeder"/> itself, against databases it builds from nothing:
/// empty file-backed SQLite → schema → identity seed → full deterministic FMS dataset.
///
/// This class once also hosted eight post-seed scenario tests (upgrade authority and chronology, closure
/// preservation, effectivity, concurrency, collision handling, suspect coverage). Their subject is what
/// happens after a valid seed already exists, and the fresh full rebuild was merely expensive setup — the
/// serial chain reached roughly 49 of the Infrastructure lane's 50 CI minutes (#891). Those tests now
/// qualify the same production behaviour against private copies of the once-seeded showcase template in
/// <see cref="FmsShowcaseScenarioTests"/>.
///
/// What remains here cannot start from a copy of the seeder's own output: the exact-dataset proof that the
/// seeder constructs the complete controlled record from an empty database, the identity-ordering proof
/// that a seed run against a pre-existing identity directory freezes the real accounts and is not drifted
/// by a second run, and the atomicity proof that a failed late-actor preflight rolls back every FMS row.
/// </summary>
public sealed class FmsShowcaseSeederTests
{
    private static readonly (ProjectLeadershipPosition Position, string UserName, ProgramRole RequiredRole)[]
        ExpectedLeadership =
        [
            (ProjectLeadershipPosition.ProjectEngineer, "project.lead", ProgramRole.ProjectEngineer),
            (ProjectLeadershipPosition.ProgramManager, "program.manager", ProgramRole.ProgramManager),
            (ProjectLeadershipPosition.EngineeringManager, "engineering.manager", ProgramRole.EngineeringManager),
            (ProjectLeadershipPosition.ConfigurationManager, "cm.fms", ProgramRole.ConfigurationManager),
            (ProjectLeadershipPosition.SystemEngineeringLead, "systems.lead", ProgramRole.SystemEngineer),
            (ProjectLeadershipPosition.SoftwareEngineeringLead, "software.lead", ProgramRole.SoftwareEngineer),
            (ProjectLeadershipPosition.SystemTestLead, "test.engineer", ProgramRole.SystemTestEngineer),
            (ProjectLeadershipPosition.SoftwareTestLead, "test.author", ProgramRole.SoftwareTestEngineer),
        ];

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
            var leadershipAccounts = await db.UserAccounts.AsNoTracking()
                .Where(x => ExpectedLeadership.Select(expected => expected.UserName).Contains(x.UserName))
                .ToDictionaryAsync(x => x.UserName, StringComparer.OrdinalIgnoreCase);
            var firstLeadership = await db.ProjectLeadershipAssignments.AsNoTracking()
                .Where(x => x.ProgramId == summary.ProgramId).OrderBy(x => x.Position).ToListAsync();
            Assert.Equal(8, firstLeadership.Count);
            foreach (var expected in ExpectedLeadership)
            {
                var account = leadershipAccounts[expected.UserName];
                var assignment = Assert.Single(firstLeadership, x => x.Position == expected.Position);
                Assert.Equal(account.Id, assignment.HolderUserId);
                Assert.Equal("system.bootstrap", assignment.AssignedBy);
                Assert.Equal(new DateTimeOffset(2024, 1, 8, 14, 0, 0, TimeSpan.Zero), assignment.AssignedAt);
                Assert.True(await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.ProgramId == summary.ProgramId
                    && x.UserId == account.Id && x.Role == expected.RequiredRole && x.EndedAt == null));
            }
            var freshMemberships = await db.ProgramMemberships.AsNoTracking()
                .Where(x => x.ProgramId == summary.ProgramId && x.EndedAt == null).ToListAsync();
            Assert.DoesNotContain(freshMemberships, membership => RetiredGrantRoles.IsRetiredGrant(membership.Role));
            Assert.DoesNotContain(freshMemberships, membership => SingularProgramRoles.IsSingular(membership.Role));
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
                    ? new[] { report.ResponsibleEngineerId, report.ResponsibleEngineerId, "project.lead", report.ResponsibleEngineerId,
                        report.ResponsibleEngineerId, report.ResponsibleEngineerId, "test.engineer", "quality.analyst" }
                    : new[] { report.ResponsibleEngineerId, report.ResponsibleEngineerId, "project.lead", report.ResponsibleEngineerId,
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
            Assert.Equal(firstLeadership.Select(x => (x.Id, x.Position, x.HolderUserId, x.AssignedAt, x.EndedAt)),
                (await db.ProjectLeadershipAssignments.AsNoTracking().Where(x => x.ProgramId == summary.ProgramId)
                    .OrderBy(x => x.Position).ToListAsync())
                .Select(x => (x.Id, x.Position, x.HolderUserId, x.AssignedAt, x.EndedAt)));
            Assert.All(await seeder.CheckInvariantsAsync(summary.ProgramId), x => Assert.True(x.Holds, $"{x.Key}: {x.Detail}"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Fresh_seed_rolls_back_every_Fms_row_when_late_actor_preflight_fails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-atomic-fresh-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new AeroLinkDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                await new IdentitySeeder(db).EnsureSeededAsync();
                var lead = await db.UserAccounts.SingleAsync(x => x.UserName == "lead.reviewer");
                lead.Disable(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync();

                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => new FmsShowcaseSeeder(db).EnsureSeededAsync());
                Assert.Contains("lead.reviewer", failure.Message, StringComparison.OrdinalIgnoreCase);
            }

            await using var verification = new AeroLinkDbContext(options);
            Assert.False(await verification.Programs.AnyAsync(x => x.Code == "FMSLIVE"));
            Assert.False(await verification.Projects.AnyAsync(x => x.Name == "FMS Product Development"));
            Assert.Empty(await verification.ShowcaseUpgradeSteps.ToListAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
