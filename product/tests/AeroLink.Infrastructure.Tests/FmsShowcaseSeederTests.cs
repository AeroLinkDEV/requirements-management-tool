using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class FmsShowcaseSeederTests
{
    [Fact]
    public async Task Generates_exact_released_15_baseline_and_active_16_work_without_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-showcase-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options); await db.Database.EnsureCreatedAsync();
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
            Assert.True(resolvedLadder.AgreesWithLegacyDefault());
            Assert.Equal(ProjectLadderConfigurationClassification.LegacyDefault, ladder.Classification);
            Assert.Equal(ProjectLadderConfigurationState.Stored, ladder.State);
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
            Assert.Equal(8, await active.CountAsync()); Assert.Equal(2, await active.CountAsync(x => x.State == ChangeRequestState.SelectedForBaseline));
            Assert.Equal(1, await active.CountAsync(x => x.State == ChangeRequestState.Approved)); Assert.Equal(1, await active.CountAsync(x => x.State == ChangeRequestState.InReview)); Assert.Equal(3, await active.CountAsync(x => x.State == ChangeRequestState.Draft)); Assert.Equal(1, await active.CountAsync(x => x.State == ChangeRequestState.Deferred));
            var codeRecords = await db.CodeTraceabilityRecords.AsNoTracking().ToListAsync();
            Assert.Equal(9, codeRecords.Count);
            Assert.Equal(5, codeRecords.Count(x => x.ReleaseId != first.ActiveReleaseId));
            Assert.Equal(4, codeRecords.Count(x => x.ReleaseId == first.ActiveReleaseId));
            Assert.All(codeRecords, x => Assert.True(x.IsDemonstration));
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
