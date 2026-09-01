using AeroLink.Domain.Requirements;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// The seeder returned early whenever a showcase Program already existed, so every invariant added after a
/// database was first seeded never reached it. A live installation held two approved FMS 1.6 change requests
/// and an empty verification-impact queue — the one state the product says is impossible — because the code
/// that raises those items shipped after that database was created.
///
/// The existing seeder test seeds a fresh database and calls the current seeder twice, which cannot see this:
/// both passes are the current version. These start from a database made to look like an older seed.
///
/// Each of these four seeded the showcase itself to reach that starting position — 26 to 61 seconds each, 160
/// seconds for a dataset that was identical all four times and that none of them set out to prove. They take a
/// copy of the shared template instead. What they exercise is unchanged: each still owns a private database and
/// rewinds, interrupts and upgrades it without any other test seeing it.
/// </summary>
[Collection(ShowcaseCollection.Name)]
public sealed class ShowcaseUpgradeTests(ShowcaseDatabaseFixture showcase)
{
    /// <summary>
    /// Rewinds a current database to the shape an older seed left behind: the verification-impact work gone,
    /// and no record of any upgrade step having run.
    /// </summary>
    private static async Task RewindToPriorVersionAsync(AeroLinkDbContext db)
    {
        db.VerificationImpactDecisionHistory.RemoveRange(await db.VerificationImpactDecisionHistory.ToListAsync());
        db.VerificationImpactItems.RemoveRange(await db.VerificationImpactItems.ToListAsync());
        db.TestChangeReviews.RemoveRange(await db.TestChangeReviews.ToListAsync());
        db.ShowcaseUpgradeSteps.RemoveRange(await db.ShowcaseUpgradeSteps.ToListAsync());
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_older_database_is_reconciled_rather_than_left_in_a_state_the_product_calls_impossible()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;

        await RewindToPriorVersionAsync(db);

        // The defect exactly as reported: approved changes, and nothing in the queue.
        Assert.Empty(await db.VerificationImpactItems.ToListAsync());
        var broken = await seeder.CheckInvariantsAsync(summary.ProgramId);
        Assert.False(broken.Single(x => x.Key == "verification-impact").Holds);

        var applied = await seeder.UpgradeAsync(summary.ProgramId);
        Assert.Contains(applied, x => x.StartsWith("verification-impact"));
        Assert.NotEmpty(await db.VerificationImpactItems.ToListAsync());

        var healthy = await seeder.CheckInvariantsAsync(summary.ProgramId);
        Assert.All(healthy, x => Assert.True(x.Holds, $"{x.Key}: {x.Detail}"));
    }

    [Fact]
    public async Task Re_running_the_upgrade_changes_nothing_and_records_each_step_once()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;
        await RewindToPriorVersionAsync(db);
        await seeder.UpgradeAsync(summary.ProgramId);

        var impacts = await db.VerificationImpactItems.CountAsync();
        var procedures = await db.TestProcedures.CountAsync();

        // Twice more, including a full re-seed, which is what a restart actually does.
        Assert.Empty(await seeder.UpgradeAsync(summary.ProgramId));
        await seeder.EnsureSeededAsync();

        Assert.Equal(impacts, await db.VerificationImpactItems.CountAsync());
        Assert.Equal(procedures, await db.TestProcedures.CountAsync());
        var steps = await db.ShowcaseUpgradeSteps.AsNoTracking().ToListAsync();
        Assert.Equal(steps.Select(x => x.StepKey).Distinct().Count(), steps.Count);
        Assert.Single(await db.Programs.ToListAsync());
    }

    [Fact]
    public async Task A_stale_pending_legacy_closure_candidate_stays_fail_closed()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;
        var reportId = Guid.Parse(await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == summary.ProgramId && x.StepKey == "scenario-richness/problem-report/06")
            .Select(x => x.Detail).SingleAsync());
        var original = await db.ProblemReportClosureCandidates.SingleAsync(x => x.ProblemReportId == reportId
            && x.State == ProblemReportClosureCandidateState.Pending);
        var originalId = original.Id;
        var originalRevisionCount = await db.ProblemReportRevisions.CountAsync(x => x.ProblemReportId == reportId);

        // PostgreSQL can round a freshly-written DateTimeOffset to microseconds. Simulate the resulting
        // legacy pending candidate whose immutable evidence hash no longer matches the persisted execution.
        db.Entry(original).Property(x => x.VerificationEvidenceHash).CurrentValue = new string('0', 64);
        await db.SaveChangesAsync();

        var executionCount = await db.TestExecutions.CountAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seeder.UpgradeAsync(summary.ProgramId));
        Assert.Contains("pr_closure_candidate_stale", failure.Message, StringComparison.Ordinal);
        var persisted = await db.ProblemReportClosureCandidates.AsNoTracking()
            .SingleAsync(x => x.Id == originalId);
        Assert.Equal(ProblemReportClosureCandidateState.Pending, persisted.State);
        Assert.Equal(new string('0', 64), persisted.VerificationEvidenceHash);
        Assert.Equal(originalRevisionCount,
            await db.ProblemReportRevisions.CountAsync(x => x.ProblemReportId == reportId));
        Assert.True(await db.ShowcaseUpgradeSteps.AnyAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness"));
        Assert.Equal(executionCount, await db.TestExecutions.CountAsync());
    }

    /// <summary>
    /// An upgrade that stops half way must resume, not restart. Each step records itself only after its own
    /// work commits, so the record is evidence the step finished rather than that it was attempted.
    /// </summary>
    [Fact]
    public async Task An_interrupted_upgrade_resumes_at_the_step_it_stopped_on()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;
        await RewindToPriorVersionAsync(db);

        // Stopped after the first two steps: their rows exist, the impact work does not.
        db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(summary.ProgramId, "release-campaign", "partial", DateTimeOffset.UtcNow));
        db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(summary.ProgramId, "product-line", "partial", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var applied = await seeder.UpgradeAsync(summary.ProgramId);
        Assert.DoesNotContain(applied, x => x.StartsWith("release-campaign"));
        Assert.DoesNotContain(applied, x => x.StartsWith("product-line"));
        Assert.Contains(applied, x => x.StartsWith("verification-impact"));
        Assert.All(await seeder.CheckInvariantsAsync(summary.ProgramId), x => Assert.True(x.Holds, $"{x.Key}: {x.Detail}"));
    }

    /// <summary>A reconciliation that discards somebody's work is worse than the gap it repairs.</summary>
    [Fact]
    public async Task User_authored_records_survive_the_upgrade()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;

        var mine = new ProblemReport(summary.ProjectId, "PR-90001", "Authored by a user", "Problem", "Analysis",
            "someone", DateTimeOffset.UtcNow, "Engineering anomaly",
            ProblemReportSeverity.Major,
            ProblemReportPriority.Normal, "Manual report", "");
        db.ProblemReports.Add(mine);
        await db.SaveChangesAsync();

        await RewindToPriorVersionAsync(db);
        await seeder.UpgradeAsync(summary.ProgramId);
        await seeder.EnsureSeededAsync();

        Assert.NotNull(await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(x => x.ReportNumber == "PR-90001"));
    }
}
