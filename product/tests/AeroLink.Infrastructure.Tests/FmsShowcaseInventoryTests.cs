using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

[Collection(ShowcaseCollection.Name)]
public sealed class FmsShowcaseInventoryTests(ShowcaseDatabaseFixture showcase)
{
    private async Task<JsonElement> ReadAsync(AeroLinkDbContext db) =>
        JsonSerializer.SerializeToElement(await new FmsShowcaseSeeder(db).InventoryAsync(showcase.Summary.ProgramId));

    [Fact]
    public async Task Legacy_mixed_software_history_is_visible_without_inventing_a_governed_family_or_off_ladder_state()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var high = await db.SystemChangeRequests.Include(x => x.RequirementChanges).FirstAsync(x =>
            x.TargetReleaseId == showcase.Summary.ActiveReleaseId && x.Type == ChangeRequestType.Software
            && x.SoftwareLevel == RequirementLevel.HighLevel);
        var low = await db.SystemChangeRequests.Include(x => x.RequirementChanges).FirstAsync(x =>
            x.TargetReleaseId == showcase.Summary.ActiveReleaseId && x.Type == ChangeRequestType.Software
            && x.SoftwareLevel == RequirementLevel.LowLevel);
        // Reproduce the retained pre-ScopeSoftwareDrafts shape in this disposable fixture.
        db.Entry(high).Property(x => x.SoftwareLevel).CurrentValue = null;
        db.Entry(low.RequirementChanges.First()).Property(x => x.ChangeRequestId).CurrentValue = high.Id;
        await db.SaveChangesAsync();
        var inventory = await ReadAsync(db);
        var build = Assert.Single(inventory.GetProperty("Builds").EnumerateArray(), x => x.GetProperty("Version").GetString() == "1.6");
        var family = Assert.Single(build.GetProperty("Families").EnumerateArray(), x => x.GetProperty("Family").GetString() == "Legacy unscoped software change requests");
        Assert.Equal(1, family.GetProperty("Count").GetInt32());
        Assert.Equal(high.Id, Assert.Single(family.GetProperty("Examples").EnumerateArray()).GetProperty("Id").GetGuid());
        var trace = Assert.Single(inventory.GetProperty("Traces").EnumerateArray(), x => x.GetProperty("Version").GetString() == "1.6").GetProperty("ChangeControl");
        var legacy = Assert.Single(trace.GetProperty("LegacyUnscopedSoftware").EnumerateArray());
        Assert.Equal(high.Id, legacy.GetProperty("Id").GetGuid());
        Assert.Equal(new[] { "HighLevel", "LowLevel" }, legacy.GetProperty("AuthoredLevels").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain(trace.GetProperty("OffLadder").EnumerateArray(), x => x.GetProperty("Id").GetGuid() == high.Id);
        Assert.Equal(trace.GetProperty("Total").GetInt32(), trace.GetProperty("OnLadder").GetInt32()
            + trace.GetProperty("OffLadder").GetArrayLength() + trace.GetProperty("LegacyUnscopedSoftware").GetArrayLength());
        var highFamily = Assert.Single(build.GetProperty("Families").EnumerateArray(), x => x.GetProperty("Family").GetString() == "Change requests/HighLevel");
        Assert.DoesNotContain(highFamily.GetProperty("Examples").EnumerateArray(), x => x.GetProperty("Id").GetGuid() == high.Id);
    }

    [Fact]
    public async Task Legacy_compatibility_counts_remain_explicitly_non_exact_on_every_inventory_surface()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == showcase.Summary.ReleasedBaselineId);
        db.Entry(baseline).Property(x => x.TestProceduresMaterializedAt).CurrentValue = null;
        var release = await db.Releases.SingleAsync(x => x.Id == baseline.ReleaseId);
        db.Entry(release).Property(x => x.ReleasedAt).CurrentValue = null;
        await db.SaveChangesAsync();
        var inventory = await ReadAsync(db);
        var build = Assert.Single(inventory.GetProperty("Builds").EnumerateArray(), x => x.GetProperty("Version").GetString() == "1.5");
        var provenance = Assert.Single(build.GetProperty("VerificationScopes").EnumerateArray());
        Assert.Equal(baseline.Id, provenance.GetProperty("BaselineId").GetGuid());
        Assert.False(provenance.GetProperty("IsExactManifest").GetBoolean());
        Assert.True(provenance.GetProperty("ResolvedRevisionCount").GetInt32() > 0);
        foreach (var row in build.GetProperty("Families").EnumerateArray().Where(x =>
                     x.GetProperty("Family").GetString()!.StartsWith("Verification/", StringComparison.Ordinal)))
            Assert.Contains("Legacy compatibility selection; not an exact", row.GetProperty("Scope").GetString());
        var trace = Assert.Single(inventory.GetProperty("Traces").EnumerateArray(), x => x.GetProperty("Version").GetString() == "1.5");
        var coverage = trace.GetProperty("RequirementCoverage");
        Assert.Contains("Legacy compatibility selection; not an exact", coverage.GetProperty("Scope").GetString());
        Assert.False(Assert.Single(coverage.GetProperty("VerificationScopes").EnumerateArray()).GetProperty("IsExactManifest").GetBoolean());
    }

    [Theory]
    [InlineData(TestProcedureLevel.HighLevel)]
    [InlineData(TestProcedureLevel.LowLevel)]
    public async Task Inventory_and_trace_invariant_follow_exact_case_coverage_after_executable_rebinding(TestProcedureLevel level)
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream).SingleAsync(x => x.ProjectId == showcase.Summary.ProjectId);
        var resolved = ProjectLadderResolver.Resolve(configuration, LegacyLadderPolicy.Instance);
        var policy = new ResolvedProjectLadderPolicy(resolved with
        {
            Steps = resolved.Steps.Select(x => x.Level is RequirementLevel.HighLevel or RequirementLevel.LowLevel
                ? x with { EnabledArtifactKinds = [VerificationArtifactKind.Case, VerificationArtifactKind.Procedure] } : x).ToArray()
        }, LegacyLadderPolicy.Instance);
        var original = await (from member in db.BaselineTestProcedures
            join sourceArtifact in db.TestProcedures on member.ProcedureId equals sourceArtifact.Id
            where member.BaselineId == showcase.Summary.ReleasedBaselineId && sourceArtifact.Level == level
            orderby sourceArtifact.BaseNumber select member).FirstAsync();
        var caseRevisionId = original.RevisionId;
        var now = DateTimeOffset.UtcNow;
        var prefix = level == TestProcedureLevel.HighLevel ? "HLRTP" : "LLRTP";
        var migrationActor = VerificationArtifactProfileSchema.GovernedMigrationActor;
        var artifact = new TestProcedure(showcase.Summary.ProjectId, $"{prefix}-990101", "Exact migrated coverage fixture",
            migrationActor, now, level, policy, VerificationArtifactKind.Procedure, VerificationProcedureParentKind.Allocated);
        var revision = new TestProcedureRevision(artifact.Id, 0, "Objective", "Preconditions", "Steps", "Expected",
            TestProcedureState.Approved, migrationActor, now, effectiveBaselineId: showcase.Summary.ReleasedBaselineId,
            environmentSetup: "Setup", testData: "Data", orderedSteps: "Steps", expectedObservations: "Expected",
            cleanup: "Cleanup", toolingAutomation: "Tooling", parentKind: VerificationProcedureParentKind.Allocated);
        db.AddRange(artifact, revision, new TestCaseProcedureLink(caseRevisionId, revision.Id),
            new TestProcedureMigrationSource(showcase.Summary.ProjectId, caseRevisionId, artifact.Id, revision.Id));
        original.RebindMigrationExecutable(artifact.Id, revision.Id);
        await db.SaveChangesAsync();
        var seeder = new FmsShowcaseSeeder(db, new FixedProjectLadderPolicyResolver(policy));
        var inventory = JsonSerializer.SerializeToElement(await seeder.InventoryAsync(showcase.Summary.ProgramId));
        var trace = Assert.Single(inventory.GetProperty("Traces").EnumerateArray(), x => x.GetProperty("Version").GetString() == "1.5");
        Assert.Equal(1248, trace.GetProperty("RequirementCoverage").GetProperty("Settled").GetInt32());
        Assert.Equal(0, trace.GetProperty("RequirementCoverage").GetProperty("Uncovered").GetInt32());
        var invariant = Assert.Single(await seeder.CheckInvariantsAsync(showcase.Summary.ProgramId), x => x.Key == "trace-gap-inventory");
        Assert.True(invariant.Holds, invariant.Detail);
    }

    [Fact]
    public async Task Inventory_separates_materialized_membership_from_active_authoring_without_inheriting_counts()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var inventory = await ReadAsync(db);
        var builds = inventory.GetProperty("Builds").EnumerateArray().ToList();
        var released = Assert.Single(builds, x => x.GetProperty("Version").GetString() == "1.5");
        var active = Assert.Single(builds, x => x.GetProperty("Version").GetString() == "1.6");
        Assert.True(released.GetProperty("RequirementsMaterialized").GetBoolean());
        Assert.True(Assert.Single(released.GetProperty("VerificationScopes").EnumerateArray()).GetProperty("IsExactManifest").GetBoolean());
        Assert.False(active.GetProperty("RequirementsMaterialized").GetBoolean());
        static int Count(JsonElement build, string prefix) => build.GetProperty("Families").EnumerateArray()
            .Where(x => x.GetProperty("Family").GetString()!.StartsWith(prefix, StringComparison.Ordinal))
            .Sum(x => x.GetProperty("Count").GetInt32());
        Assert.Equal(1250, Count(released, "Requirements/"));
        Assert.Equal(0, Count(active, "Requirements/"));
        Assert.Equal(10 + FmsShowcaseSeeder.ActiveTraceRequestCount, Count(active, "Change requests/"));
        Assert.Equal(0, Count(active, "Verification/"));
        var trace = Assert.Single(inventory.GetProperty("Traces").EnumerateArray(), x => x.GetProperty("Version").GetString() == "1.6");
        Assert.True(trace.GetProperty("RequirementCoverage").GetProperty("WaitingForPrerequisite").GetBoolean());
        Assert.Equal(0, trace.GetProperty("RequirementCoverage").GetProperty("Total").GetInt32());
    }

    [Fact]
    public async Task Inventory_reports_retained_off_ladder_history_without_asking_the_ladder_to_classify_it()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var request = await db.SystemChangeRequests.FirstAsync(x => x.TargetReleaseId == showcase.Summary.ActiveReleaseId
            && x.Type == ChangeRequestType.System);
        // Reproduce a retained row from before Interface retirement; this is private fixture state.
        db.Entry(request).Property(x => x.Type).CurrentValue = ChangeRequestType.Interface;
        await db.SaveChangesAsync();
        var inventory = await ReadAsync(db);
        var trace = Assert.Single(inventory.GetProperty("Traces").EnumerateArray(), x => x.GetProperty("Version").GetString() == "1.6");
        var changes = trace.GetProperty("ChangeControl");
        Assert.Equal(10 + FmsShowcaseSeeder.ActiveTraceRequestCount, changes.GetProperty("Total").GetInt32());
        Assert.Equal(9 + FmsShowcaseSeeder.ActiveTraceRequestCount, changes.GetProperty("OnLadder").GetInt32());
        var retained = Assert.Single(changes.GetProperty("OffLadder").EnumerateArray());
        Assert.Equal(request.Id, retained.GetProperty("Id").GetGuid());
        Assert.Equal(request.DisplayNumber, retained.GetProperty("DisplayNumber").GetString());
    }

    [Fact]
    public async Task Inventory_does_not_substitute_global_coverage_when_the_exact_manifest_is_empty()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        await db.BaselineTestProcedures.Where(x => x.BaselineId == showcase.Summary.ReleasedBaselineId).ExecuteDeleteAsync();
        var inventory = await ReadAsync(db);
        var trace = Assert.Single(inventory.GetProperty("Traces").EnumerateArray(), x => x.GetProperty("Version").GetString() == "1.5");
        var coverage = trace.GetProperty("RequirementCoverage");
        Assert.Equal(1250, coverage.GetProperty("Total").GetInt32());
        Assert.Equal(0, coverage.GetProperty("Settled").GetInt32());
        Assert.Equal(1250, coverage.GetProperty("Uncovered").GetInt32());
    }
}
