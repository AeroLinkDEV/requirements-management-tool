using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

[Collection(ShowcaseCollection.Name)]
public sealed class FmsShowcaseActiveTraceTests(ShowcaseDatabaseFixture showcase)
{
    [Fact]
    public async Task Current_build_trace_population_has_named_native_gaps_and_idempotent_draft_enrichment()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var inventory = await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId);
        Assert.True(inventory.Holds, string.Join(" ", inventory.Problems));
        Assert.Equal(8 + FmsShowcaseSeeder.ActiveTraceDraftCount, inventory.CurrentChanges);
        Assert.InRange(inventory.IncompletePercent, 5, 10);
        Assert.All(inventory.Records.Where(x => x.Overall == "ActionRequired"), x => Assert.True(x.NamedNegative));
        var ownedIds = await db.ShowcaseUpgradeSteps.Where(x => x.ProgramId == showcase.Summary.ProgramId
            && x.StepKey.StartsWith(FmsShowcaseSeeder.ActiveTraceScenarioPrefix)
            && !x.StepKey.Contains("observed")).Select(x => x.Detail).ToListAsync();
        var ids = ownedIds.Select(Guid.Parse).ToList();
        Assert.Equal(FmsShowcaseSeeder.ActiveTraceDraftCount, ids.Distinct().Count());
        var drafts = await db.SystemChangeRequests.Where(x => ids.Contains(x.Id)).ToListAsync();
        Assert.All(drafts, x =>
        {
            Assert.Equal(ChangeRequestState.Draft, x.State);
            Assert.Equal(showcase.Summary.ActiveReleaseId, x.TargetReleaseId);
            Assert.NotEqual("admin", x.AuthorId);
            Assert.True(x.CreatedAt <= DateTimeOffset.UtcNow);
        });
        Assert.Empty(await seeder.UpgradeAsync(showcase.Summary.ProgramId));
        Assert.Equal(1250, await db.BaselineRequirements.CountAsync(x => x.BaselineId == showcase.Summary.ReleasedBaselineId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_or_wrong_positive_parent_fails_and_a_rerun_does_not_adopt_it_as_intentional(bool wrongParent)
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var programId = showcase.Summary.ProgramId;
        var marker = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == programId
            && x.StepKey == FmsShowcaseSeeder.ActiveTraceScenarioPrefix + "01/HighLevel");
        var id = Guid.Parse(marker.Detail);
        var request = await db.SystemChangeRequests.Include(x => x.UpstreamLinks).SingleAsync(x => x.Id == id);
        var link = Assert.Single(request.UpstreamLinks);
        request.RemoveUpstreamLink(request.AuthorId, link.Id, "Deliberate regression fixture.", DateTimeOffset.UtcNow);
        if (wrongParent)
        {
            var other = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == programId
                && x.StepKey == FmsShowcaseSeeder.ActiveTraceScenarioPrefix + "02/System");
            var parentId = Guid.Parse(other.Detail);
            var parent = await db.SystemChangeRequests.SingleAsync(x => x.Id == parentId);
            request.AddUpstreamLink(request.AuthorId, parent.Id, parent.DisplayNumber, parent.TargetReleaseId,
                "1.6", "Typed but incorrect scenario parent.", DateTimeOffset.UtcNow);
        }
        await db.SaveChangesAsync();
        var seeder = new FmsShowcaseSeeder(db);
        var before = await seeder.ActiveTraceInventoryAsync(programId);
        Assert.False(before.Holds);
        Assert.Contains(before.Problems, x => x.Contains("scenario 01", StringComparison.Ordinal));
        if (!wrongParent) Assert.Contains(before.Problems, x => x.Contains(request.DisplayNumber, StringComparison.Ordinal));
        Assert.Empty(await seeder.UpgradeAsync(programId));
        Assert.False((await seeder.ActiveTraceInventoryAsync(programId)).Holds);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("revision")]
    [InlineData("upstream")]
    public async Task Editing_one_proposal_to_the_wrong_exact_requirement_fails_without_changing_its_CR_links(string field)
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var marker = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == showcase.Summary.ProgramId
            && x.StepKey == FmsShowcaseSeeder.ActiveTraceScenarioPrefix + "01/HighLevel");
        var requestId = Guid.Parse(marker.Detail);
        var request = await db.SystemChangeRequests.Include(x => x.RequirementChanges).Include(x => x.UpstreamLinks)
            .SingleAsync(x => x.Id == requestId);
        var original = Assert.Single(request.RequirementChanges);
        var upstream = JsonSerializer.Deserialize<Guid[]>(original.ProposedUpstreamRevisionIdsJson)!;
        var otherParent = await (from revision in db.RequirementRevisions
            join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
            where artifact.ProjectId == showcase.Summary.ProjectId && artifact.Level == RequirementLevel.System
                && !upstream.Contains(revision.Id) select revision.Id).FirstAsync();
        request.RemoveRequirementChange(request.AuthorId, original.Id, DateTimeOffset.UtcNow);
        request.AddRequirementChange(request.AuthorId, field == "number" ? "HLR-000002" : original.BaseNumber,
            field == "revision" ? original.Revision + 1 : original.Revision, original.Level, original.Kind,
            original.Statement, original.Rationale, original.VerificationMethod, DateTimeOffset.UtcNow,
            impactDispositionJson: original.ImpactDispositionJson,
            proposedUpstreamRevisionIdsJson: field == "upstream" ? JsonSerializer.Serialize(new[] { otherParent }) : original.ProposedUpstreamRevisionIdsJson);
        await db.SaveChangesAsync();
        Assert.Single(request.UpstreamLinks);
        var seeder = new FmsShowcaseSeeder(db);
        Assert.Contains((await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId)).Problems,
            x => x.Contains($"exact proposal of {request.DisplayNumber}"));
        Assert.Empty(await seeder.UpgradeAsync(showcase.Summary.ProgramId));
        Assert.False((await seeder.ActiveTraceInventoryAsync(showcase.Summary.ProgramId)).Holds);
    }
}
