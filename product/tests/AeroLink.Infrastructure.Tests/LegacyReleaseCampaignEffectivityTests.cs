using AeroLink.Domain.Releases;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

[Collection(ShowcaseCollection.Name)]
public sealed class LegacyReleaseCampaignEffectivityTests(ShowcaseDatabaseFixture showcase)
{
    [Theory]
    [InlineData("matching", true)]
    [InlineData("other-baseline", false)]
    [InlineData("other-release", false)]
    [InlineData("incomplete", false)]
    [InlineData("missing-hash", false)]
    [InlineData("missing-date", false)]
    [InlineData("header-precedence", false)]
    [InlineData("exact", true)]
    public async Task Legacy_window_uses_only_unambiguous_completion_of_its_own_build(string scenario, bool included)
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == showcase.Summary.ReleasedBaselineId);
        var release = await db.Releases.SingleAsync(x => x.Id == baseline.ReleaseId);
        var campaign = await db.ReleaseCampaigns.SingleAsync(x => x.ReleaseId == release.Id);
        var freeze = new DateTimeOffset(2024, 6, 7, 0, 0, 0, TimeSpan.Zero);
        db.Entry(baseline).Property(x => x.CreatedAt).CurrentValue = freeze.AddDays(-1);
        db.Entry(baseline).Property(x => x.FrozenAt).CurrentValue = freeze;
        if (scenario != "exact")
            db.Entry(baseline).Property(x => x.TestProceduresMaterializedAt).CurrentValue = null;
        db.Entry(release).Property(x => x.ReleasedAt).CurrentValue = scenario == "header-precedence" ? freeze : null;
        db.Entry(campaign).Property(x => x.ReleasedAt).CurrentValue = scenario == "missing-date" ? null : freeze.AddDays(15);
        db.Entry(campaign).Property(x => x.State).CurrentValue = scenario == "incomplete"
            ? ReleaseCampaignState.Verification : ReleaseCampaignState.Released;
        db.Entry(campaign).Property(x => x.ReleaseHash).CurrentValue = scenario == "missing-hash" ? null : new string('a', 64);
        if (scenario == "other-baseline")
        {
            var other = await db.CandidateBaselines.SingleAsync(x => x.ReleaseId == showcase.Summary.ActiveReleaseId);
            db.Entry(campaign).Property(x => x.BaselineId).CurrentValue = other.Id;
        }
        if (scenario == "other-release")
        {
            var otherRelease = new SoftwareRelease(baseline.ProjectId, "9.9", true);
            db.Add(otherRelease);
            db.Entry(campaign).Property(x => x.ReleaseId).CurrentValue = otherRelease.Id;
        }
        var selection = await (from member in db.BaselineTestProcedures
            join artifact in db.TestProcedures on member.ProcedureId equals artifact.Id
            where member.BaselineId == baseline.Id && artifact.Level == TestProcedureLevel.System
            orderby artifact.BaseNumber select member).FirstAsync();
        var revision = await db.TestProcedureRevisions.SingleAsync(x => x.Id == selection.RevisionId);
        db.Entry(revision).Property(x => x.CreatedAt).CurrentValue = freeze.AddDays(3);
        var later = new TestProcedureRevision(selection.ProcedureId, 99, "Later objective", "Preconditions",
            "Steps", "Expected", TestProcedureState.Approved, "test.author", freeze.AddDays(30));
        db.Add(later);
        var requirementId = await db.TestCoverage.Where(x => x.ProcedureRevisionId == revision.Id)
            .Select(x => x.RequirementRevisionId).FirstAsync();
        db.Add(new TestRequirementCoverage(later.Id, requirementId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = Assert.IsType<TestProcedureEffectivityResult>(
            await TestProcedureEffectivity.ForBaselineAsync(db, baseline.Id, default));
        Assert.Equal(scenario == "exact", result.IsExactManifest);
        Assert.Equal(included, result.RevisionIds.Contains(revision.Id));
        Assert.DoesNotContain(later.Id, result.RevisionIds);
        Assert.False(db.ChangeTracker.HasChanges());
        var retained = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == baseline.Id);
        Assert.Equal(scenario == "exact", retained.TestProceduresMaterializedAt is not null);
        Assert.Equal(scenario == "header-precedence" ? freeze : (DateTimeOffset?)null,
            await db.Releases.Where(x => x.Id == release.Id).Select(x => x.ReleasedAt).SingleAsync());
    }
}
