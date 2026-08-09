using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Putting approved procedure work into a build.
///
/// The materializer existed with no way to reach it, so a test change request could be authored and approved
/// and then had nowhere to go. These are about the whole chain arriving in a baseline.
/// </summary>
public sealed class ProcedureBaselineApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid BaselineId, Guid TcrId, Guid NoWorkTcrId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Procedure Baseline", "PBL");
        var project = new ProjectRecord(program.Id, "Software", "Procedure Baseline Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        var scr = new SystemChangeRequest("SRCR-00930", 0, project.Id, release.Id, "Oceanic", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", "SYSR-00000931", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "New capability", "Test", now);
        scr.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        var baseline = new CandidateBaseline("SW-00.10", 0, project.Id, release.Id, null, "Procedure baseline", "cm", now);
        baseline.Select(scr, "cm", now);
        baseline.Freeze("cm", now);
        db.AddRange(scr, baseline);

        // Two packages against one change request would collide on the exclusivity index, so the one that
        // concluded no work is raised from its own change request.
        var quiet = new SystemChangeRequest("SRCR-00931", 0, project.Id, release.Id, "Wording", "P", "A", "S", "author", now);
        quiet.AddRequirementChange("author", "SYSR-00000932", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall state the active plan.", "Wording", "Test", now);
        quiet.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        quiet.ApproveActiveStage("reviewer", now);
        db.Add(quiet);

        var carrying = new TestChangeReview(project.Id, release.Id, scr.Id,
            TestChangeReviewDiscipline.System, scr.DisplayNumber, now);
        carrying.RecordTestChangeRequired("verification.engineer", now);
        carrying.AssignControlledNumber("SYSTCR-000931", now);
        var noWork = new TestChangeReview(project.Id, release.Id, quiet.Id, TestChangeReviewDiscipline.System,
            quiet.DisplayNumber, now);
        noWork.RecordNoTestChangeRequired("verification.engineer", "Existing procedures already exercise it.", now);
        noWork.Submit("verification.engineer", "test.lead", true, now);
        noWork.Approve("test.lead", "Agreed.", now);
        db.AddRange(carrying, noWork);

        foreach (var (user, role) in new[]
                 {
                     ("baseline.cm", ProgramRole.ConfigurationManager),
                     ("baseline.reader", ProgramRole.Engineer),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, baseline.Id, carrying.Id, noWork.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task MaterializeRequirementsAsync(HttpClient client, Guid baselineId)
    {
        using var response = await client.PostAsJsonAsync($"/api/baselines/{baselineId}/materialize-requirements", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task PrepareCarryingPackageAsync(AeroLinkApiFactory factory, Fixture fixture)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
            .SingleAsync(x => x.Id == fixture.TcrId);
        var request = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .SingleAsync(x => x.Id == review.ChangeRequestId);
        var revision = await db.RequirementRevisions.SingleAsync(x => x.SourceChangeRequestId == request.Id);
        var item = VerificationImpactItem.ForIntroducedRequirement(fixture.ProjectId, fixture.ReleaseId,
            request.Id, review.Id, request.RequirementChanges.Single().Id,
            request.RequirementChanges.Single().DisplayNumber, "Test", now);
        item.LinkRequirementRevision(revision.Id, now);
        review.AddProcedureChange("verification.engineer", new TestProcedureChangeDraft("SYSTP-000931", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Introduce, "Oceanic waypoint sequencing",
            "Verify oceanic sequencing.", "Cruise.", "1. Load. 2. Read.", "Sequenced.",
            "Nothing covers oceanic sequencing.", JsonSerializer.Serialize(new[] { revision.Id })), now);
        review.WriteCase("verification.engineer", "Verification case", "Problem", "Analysis", "Solution", now);
        review.Submit("verification.engineer", "test.lead", true, now);
        review.Approve("test.lead", "Reviewed.", now);
        db.Add(item);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Approved_procedure_work_reaches_a_build_and_fixes_its_manifest()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);
        await PrepareCarryingPackageAsync(factory, fixture);

        // Selecting happens after the freeze: a procedure is written against a requirement this baseline has
        // already fixed, so it is finished later.
        using var selected = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = fixture.TcrId });
        Assert.True(selected.StatusCode == HttpStatusCode.OK, await selected.Content.ReadAsStringAsync());

        using var materialized = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });
        var body = await materialized.Content.ReadAsStringAsync();
        Assert.True(materialized.StatusCode == HttpStatusCode.OK, $"{(int)materialized.StatusCode}: {body}");

        var result = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(1, result.GetProperty("activeProcedureCount").GetInt32());
        Assert.Equal(1, result.GetProperty("createdRevisionCount").GetInt32());
        Assert.Equal(64, result.GetProperty("proceduresHash").GetString()!.Length);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var revision = await db.TestProcedureRevisions.SingleAsync();
        Assert.Equal(fixture.TcrId, revision.SourceTestChangeRequestId);
        Assert.Equal(fixture.BaselineId, revision.EffectiveBaselineId);
        Assert.Single(await db.BaselineTestProcedures.Where(x => x.BaselineId == fixture.BaselineId).ToListAsync());

        // Fixed once. A second materialization would silently rewrite what the build carries.
        using var again = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task A_package_that_found_no_work_is_not_offered_and_cannot_be_carried()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);
        await PrepareCarryingPackageAsync(factory, fixture);

        var listing = await client.GetFromJsonAsync<JsonElement>($"/api/baselines/{fixture.BaselineId}/test-change-requests");
        var available = listing.GetProperty("available").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(fixture.TcrId, available);
        // It has no procedures and no controlled number, so carrying it would imply test work that does not exist.
        Assert.DoesNotContain(fixture.NoWorkTcrId, available);

        using var refused = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = fixture.NoWorkTcrId });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("no test work was required", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Procedures_cannot_be_fixed_before_the_requirements_they_verify()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");

        using var tooEarly = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });
        Assert.Equal(HttpStatusCode.BadRequest, tooEarly.StatusCode);
        Assert.Contains("Materialize the requirement baseline", await tooEarly.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Carrying_procedure_work_into_a_build_is_a_configuration_managers_act()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.reader");

        using var refused = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = fixture.TcrId });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var alsoRefused = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });
        Assert.Equal(HttpStatusCode.Forbidden, alsoRefused.StatusCode);
    }

    [Fact]
    public async Task A_selection_can_be_withdrawn_until_the_manifest_is_fixed()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);
        await PrepareCarryingPackageAsync(factory, fixture);

        using var selected = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = fixture.TcrId });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);

        using var removed = await client.DeleteAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests/{fixture.TcrId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var listing = await client.GetFromJsonAsync<JsonElement>($"/api/baselines/{fixture.BaselineId}/test-change-requests");
        Assert.Empty(listing.GetProperty("selected").EnumerateArray());

        // Materializing with nothing selected is legitimate: the build carries whatever its predecessor did,
        // which here is nothing, and says so with a manifest rather than staying unanswered.
        using var materialized = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });
        Assert.Equal(HttpStatusCode.OK, materialized.StatusCode);
        using var afterwards = await client.DeleteAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests/{fixture.TcrId}");
        Assert.Equal(HttpStatusCode.BadRequest, afterwards.StatusCode);
    }

        private static async Task ReleaseAsync(AeroLinkApiFactory factory, Guid baselineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var baseline = await db.CandidateBaselines.SingleAsync(x => x.Id == baselineId);
        baseline.MarkReleased("cm", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    private static async Task MarkReleaseReleasedAsync(AeroLinkApiFactory factory, Guid releaseId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        await db.Releases.Where(x => x.Id == releaseId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.IsReleased, true)
                .SetProperty(x => x.ReleasedAt, DateTimeOffset.UtcNow));
    }

    private static async Task<string> SnapshotProcedureStateAsync(AeroLinkApiFactory factory, Guid baselineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var baseline = await db.CandidateBaselines.Include(x => x.TestChangeSelections).Include(x => x.Events)
            .SingleAsync(x => x.Id == baselineId);
        var selected = string.Join(",", baseline.TestChangeSelections
            .Select(x => x.TestChangeRequestId).OrderBy(x => x));
        var manifestRows = await db.BaselineTestProcedures.CountAsync(x => x.BaselineId == baselineId);
        var revisions = await db.TestProcedureRevisions.CountAsync();
        var coverage = await db.TestCoverage.CountAsync();
        var impacts = string.Join(",", (await db.VerificationImpactItems.AsNoTracking().ToListAsync())
            .Select(x => $"{x.Id}:{x.State}"));
        return $"{selected}|{manifestRows}|{baseline.TestProceduresHash}|{baseline.TestProceduresMaterializedAt}|{baseline.Events.Count}|{revisions}|{coverage}|{impacts}";
    }

    [Fact]
    public async Task A_released_baseline_refuses_selecting_a_test_change_request_without_build_context()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);
        await PrepareCarryingPackageAsync(factory, fixture);
        await ReleaseAsync(factory, fixture.BaselineId);
        var before = await SnapshotProcedureStateAsync(factory, fixture.BaselineId);

        // The TCR is otherwise eligible (approved, carrying procedure work) and NOT already selected, so on
        // unmodified main this POST succeeds; only the released baseline can refuse it.
        using var refused = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = fixture.TcrId });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Released baselines are immutable", await refused.Content.ReadAsStringAsync());

        var after = await SnapshotProcedureStateAsync(factory, fixture.BaselineId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task A_released_baseline_refuses_removing_a_test_change_request_without_build_context()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);
        await PrepareCarryingPackageAsync(factory, fixture);

        // Legitimate selection while the baseline is Frozen, then release.
        using var selected = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = fixture.TcrId });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        await ReleaseAsync(factory, fixture.BaselineId);
        var before = await SnapshotProcedureStateAsync(factory, fixture.BaselineId);

        using var refused = await client.DeleteAsync(
            $"/api/baselines/{fixture.BaselineId}/test-change-requests/{fixture.TcrId}");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Released baselines are immutable", await refused.Content.ReadAsStringAsync());

        var after = await SnapshotProcedureStateAsync(factory, fixture.BaselineId);
        Assert.Equal(before, after);
        Assert.Contains(fixture.TcrId.ToString(), after);
    }

    [Fact]
    public async Task A_released_baseline_refuses_materializing_a_manifest_without_build_context()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);
        await PrepareCarryingPackageAsync(factory, fixture);
        await ReleaseAsync(factory, fixture.BaselineId);
        var before = await SnapshotProcedureStateAsync(factory, fixture.BaselineId);

        using var refused = await client.PostAsJsonAsync(
            $"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Freeze the baseline before materializing its test procedures", await refused.Content.ReadAsStringAsync());

        var after = await SnapshotProcedureStateAsync(factory, fixture.BaselineId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task A_released_baseline_refuses_test_procedure_mutations_with_the_released_build_context_header()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "baseline.cm");
        await MaterializeRequirementsAsync(client, fixture.BaselineId);
        await PrepareCarryingPackageAsync(factory, fixture);
        await ReleaseAsync(factory, fixture.BaselineId);
        await MarkReleaseReleasedAsync(factory, fixture.ReleaseId);
        var before = await SnapshotProcedureStateAsync(factory, fixture.BaselineId);

        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", fixture.ReleaseId.ToString());
        using var refusedSelect = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = fixture.TcrId });
        var selectBody = await refusedSelect.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, refusedSelect.StatusCode);
        Assert.Contains("released_build_read_only", selectBody);

        using var refusedRemove = await client.DeleteAsync(
            $"/api/baselines/{fixture.BaselineId}/test-change-requests/{fixture.TcrId}");
        var removeBody = await refusedRemove.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, refusedRemove.StatusCode);
        Assert.Contains("released_build_read_only", removeBody);

        using var refusedMaterialize = await client.PostAsJsonAsync(
            $"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });
        var materializeBody = await refusedMaterialize.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, refusedMaterialize.StatusCode);
        Assert.Contains("released_build_read_only", materializeBody);

        var after = await SnapshotProcedureStateAsync(factory, fixture.BaselineId);
        Assert.Equal(before, after);
    }
}
