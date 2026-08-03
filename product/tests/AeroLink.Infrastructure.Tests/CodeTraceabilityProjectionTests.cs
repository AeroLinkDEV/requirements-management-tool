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
}
