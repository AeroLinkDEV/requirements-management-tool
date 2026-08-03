using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class CodeTraceabilityProjectionTests
{
    [Fact]
    public async Task Real_project_with_no_LLR_changed_in_build_owes_zero_code_mappings()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Production program", "REAL");
        var project = new ProjectRecord(program.Id, "Operational FMS", "Flight Management System");
        var previousRelease = new SoftwareRelease(project.Id, "1.5", true);
        var release = new SoftwareRelease(project.Id, "1.6", false, previousRelease.Id);
        var selectedChange = new SystemChangeRequest("SCR-900001", 0, project.Id, release.Id, "Package unchanged software", "A build package is required.", "No LLR change is needed.", "Select the exact current requirements.", "systems.author", now);
        selectedChange.AddRequirementChange("systems.author", "SYSR-900001", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The system shall preserve the approved software behavior.", "Package-only system change.", "Review", now);
        selectedChange.SubmitForReview("systems.author", [new ApproverSelection("systems.reviewer", "Systems Reviewer")], now);
        selectedChange.ApproveActiveStage("systems.reviewer", now);
        var historicalChange = new SystemChangeRequest("SWCR-900001", 0, project.Id, previousRelease.Id, "Historical LLR definition", "Define prior behavior.", "Prior analysis.", "Prior solution.", "software.author", now, ChangeRequestType.Software);
        var baseline = new CandidateBaseline("SW-90.00", 0, project.Id, release.Id, null, "No LLR changes", "cm", now);
        baseline.Select(selectedChange, "cm", now);
        baseline.Freeze("cm", now);
        baseline.MarkRequirementsMaterialized("cm", new string('b', 64), 1, now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Build 1.6 release", "program.manager", now);
        var artifact = new RequirementArtifact(project.Id, "LLR-900001", RequirementLevel.LowLevel, now);
        var revision = new RequirementRevision(artifact.Id, 0, "The software shall retain the approved behavior.", "Historical requirement.", "Test", RequirementRevisionState.Active, historicalChange.Id, baseline.Id, now);

        db.AddRange(program, project, previousRelease, release, selectedChange, historicalChange, baseline, campaign, artifact, revision,
            new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id));
        await db.SaveChangesAsync();

        var readiness = await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default);
        Assert.Contains(readiness.Gates, x => x.Code == "code_traceability" && x.Complete && x.Completed == 0 && x.Total == 0);
    }

    /// <summary>
    /// The required set is what the build changed, not what sorts first.
    ///
    /// One Program used to take the first five LLRs by number instead, which meant an authoritative gate
    /// measured requirements the build had never touched while the ones it did touch owed nothing. The
    /// requirement changed here is deliberately numbered far above five unchanged ones, so a projection that
    /// fell back to ordering would select the wrong five and miss the only one that matters.
    /// </summary>
    [Fact]
    public async Task A_changed_high_numbered_LLR_is_required_and_unchanged_low_numbered_ones_are_not()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Flight Management System Live Program", "FMSLIVE");
        var project = new ProjectRecord(program.Id, "FMS Product Development", "Flight Management System");
        var previousRelease = new SoftwareRelease(project.Id, "1.5", true);
        var release = new SoftwareRelease(project.Id, "1.6", false, previousRelease.Id);
        var historicalChange = new SystemChangeRequest("SWCR-800001", 0, project.Id, previousRelease.Id, "Historical LLRs", "P", "A", "S", "software.author", now, ChangeRequestType.Software);
        var buildChange = new SystemChangeRequest("SWCR-800002", 0, project.Id, release.Id, "Change one LLR in this build", "P", "A", "S", "software.author", now, ChangeRequestType.Software);
        buildChange.AddRequirementChange("software.author", "LLR-000736", 0, RequirementLevel.LowLevel, RequirementChangeKind.Introduce,
            "The software shall apply the corrected oceanic sequencing.", "Introduced by this build.", "Test", now);
        buildChange.SubmitForReview("software.author", [new ApproverSelection("software.reviewer", "Software Reviewer")], now);
        buildChange.ApproveActiveStage("software.reviewer", now);
        var baseline = new CandidateBaseline("SW-01.60", 0, project.Id, release.Id, null, "Build 1.6", "cm", now);
        baseline.Select(buildChange, "cm", now);
        baseline.Freeze("cm", now);
        baseline.MarkRequirementsMaterialized("cm", new string('c', 64), 6, now);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Build 1.6 release", "program.manager", now);
        db.AddRange(program, project, previousRelease, release, historicalChange, buildChange, baseline, campaign);

        // Five unchanged LLRs that sort first, and one changed LLR that sorts last.
        foreach (var number in new[] { 1, 2, 3, 4, 5 })
        {
            var carried = new RequirementArtifact(project.Id, $"LLR-00000{number}", RequirementLevel.LowLevel, now);
            var carriedRevision = new RequirementRevision(carried.Id, 0, "Behavior carried forward unchanged.", "Historical.", "Test", RequirementRevisionState.Active, historicalChange.Id, baseline.Id, now);
            db.AddRange(carried, carriedRevision, new BaselineRequirementSelection(baseline.Id, carried.Id, carriedRevision.Id));
        }
        var changed = new RequirementArtifact(project.Id, "LLR-000736", RequirementLevel.LowLevel, now);
        var changedRevision = new RequirementRevision(changed.Id, 0, "Behavior introduced by this build.", "Changed here.", "Test", RequirementRevisionState.Active, buildChange.Id, baseline.Id, now);
        db.AddRange(changed, changedRevision, new BaselineRequirementSelection(baseline.Id, changed.Id, changedRevision.Id));
        await db.SaveChangesAsync();

        var required = await CodeTraceabilityProjection.RequiredAsync(db, project.Id, release.Id, baseline.Id, default);

        var owed = Assert.Single(required);
        Assert.Equal("LLR-000736", owed.BaseNumber);
        Assert.True(owed.ChangedInBuild);

        // And the gate agrees: one owed, none mapped, not complete. A changed LLR cannot be omitted while the
        // gate reports complete.
        var readiness = await new ReleaseReadinessService(db).CalculateAsync(campaign.Id, default);
        var gate = Assert.Single(readiness.Gates, x => x.Code == "code_traceability");
        Assert.False(gate.Complete);
        Assert.Equal(0, gate.Completed);
        Assert.Equal(1, gate.Total);
    }
}
