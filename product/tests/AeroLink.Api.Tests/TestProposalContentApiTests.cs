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
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The proposed content of a controlled Test Change Request.
///
/// A separate resource from the requirement one, and deliberately so: a TestChangeReview is a different
/// aggregate carrying TestProcedureChange rows, and ChangeRequestType has no Test member. What these prove is
/// that the verification content arrives as verification content — structured procedure fields, exact
/// predecessors, and requirement coverage kept apart from Case/Procedure parentage.
/// </summary>
public sealed class TestProposalContentApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;
    public TestProposalContentApiTests(SharedApiHost host) => _host = host;

    private const string SupersededSteps = "Enter three oceanic waypoints and observe the sequence.";
    private const string LatestSteps = "Enter five oceanic waypoints and observe the sequence.";
    private const string ProposedSteps = "Enter five oceanic waypoints and observe round-robin sequencing.";

    private sealed record Fixture(Guid ProjectId, Guid SystemTcrId, Guid CaseTcrId, Guid ProcedureTcrId,
        Guid IntroduceId, Guid ModifyId, Guid RetireId, Guid CaseIntroduceId, Guid ProcedureIntroduceId,
        Guid CoveredRevisionId, Guid RemovedRevisionId, Guid CaseParentRevisionId,
        string Member, string Outsider);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var memberName = $"tcr.member.{tag}";
        var outsiderName = $"tcr.outsider.{tag}";
        var program = new ProgramRecord($"Test proposal {tag}", $"TP{tag}");
        var project = new ProjectRecord(program.Id, "Flight management", "TCR proposal qualification");
        var release = new SoftwareRelease(project.Id, "4.1", false);
        var member = new UserAccount(memberName, memberName, $"{memberName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var outsider = new UserAccount(outsiderName, outsiderName, $"{outsiderName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release, member, outsider,
            new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "test.setup", now));

        var scr = new SystemChangeRequest("SRCR-92001", 0, project.Id, release.Id, "Sequencing rework",
            "Problem", "Analysis", "Solution", memberName, now);
        db.Add(scr);

        var baseline = new CandidateBaseline("SW-92.00", 0, project.Id, release.Id, null, "Origin", "cm", now);
        db.Add(baseline);

        // Two requirement revisions: one this package proposes to cover, one whose coverage it removes.
        var covered = new RequirementArtifact(project.Id, "SR-92001", RequirementLevel.System, now);
        var coveredRevision = new RequirementRevision(covered.Id, 0,
            "The FMS shall sequence oceanic waypoints in round-robin order.", "Rationale", "Test",
            RequirementRevisionState.Active, scr.Id, baseline.Id, now);
        var dropped = new RequirementArtifact(project.Id, "SR-92002", RequirementLevel.System, now);
        var droppedRevision = new RequirementRevision(dropped.Id, 0,
            "The FMS shall sequence oceanic waypoints in fixed order.", "Rationale", "Test",
            RequirementRevisionState.Active, scr.Id, baseline.Id, now);
        db.AddRange(covered, coveredRevision, dropped, droppedRevision);

        // The controlled procedure the Modify targets, with a LATER revision that says something different.
        // This is the whole point: a proposal written against revision 0 must be shown against revision 0.
        var procedure = new TestProcedure(project.Id, "SYSTP-92001", "Oceanic sequencing", memberName, now,
            TestProcedureLevel.System, null, VerificationArtifactKind.Procedure);
        var supersededRevision = new TestProcedureRevision(procedure.Id, 0, "Verify sequencing",
            "Configured product.", SupersededSteps, "The sequence is correct.",
            TestProcedureState.Approved, memberName, now);
        var latestRevision = new TestProcedureRevision(procedure.Id, 1, "Verify sequencing",
            "Configured product.", LatestSteps, "The sequence is correct.",
            TestProcedureState.Approved, memberName, now);
        db.AddRange(procedure, supersededRevision, latestRevision);

        var driving = JsonSerializer.Serialize(new[] { coveredRevision.Id });
        var removed = JsonSerializer.Serialize(new[] { droppedRevision.Id });

        // A System Procedure package: Introduce, Modify against the exact earlier revision, and Retire.
        var systemTcr = new TestChangeReview(project.Id, release.Id, scr.Id,
            new VerificationArtifactKey(VerificationDiscipline.System, VerificationArtifactKind.Procedure),
            "SRCR-92001.00", now, "SYSTPCR-92001", 0);
        systemTcr.RecordTestChangeRequired(memberName, now);
        db.Add(systemTcr);
        var introduce = systemTcr.AddProcedureChange(memberName, new TestProcedureChangeDraft("SYSTP-92002", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Introduce, "Round-robin sequencing",
            "Verify round-robin sequencing.", "Configured product.", ProposedSteps,
            "Sequencing is round-robin.", "New coverage.", driving), now);
        var modify = systemTcr.AddProcedureChange(memberName, new TestProcedureChangeDraft("SYSTP-92001", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Modify, "Oceanic sequencing",
            "Verify sequencing.", "Configured product.", ProposedSteps, "The sequence is correct.",
            "Reworked for round-robin.", driving, removed), now);
        var retire = systemTcr.AddProcedureChange(memberName, new TestProcedureChangeDraft("SYSTP-92003", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Retire, "", "", "", "", "",
            "No longer applicable."), now);

        // A software Case package, and a software Procedure package whose exact parent is a Case revision.
        var caseTcr = new TestChangeReview(project.Id, release.Id, scr.Id,
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case),
            "SRCR-92001.00", now, "HLRTCCR-92001", 0);
        caseTcr.RecordTestChangeRequired(memberName, now);
        db.Add(caseTcr);
        var caseIntroduce = caseTcr.AddProcedureChange(memberName, new TestProcedureChangeDraft("HLRTC-92001", 0,
            TestProcedureLevel.HighLevel, TestProcedureChangeKind.Introduce, "Waypoint case",
            "Cover round-robin selection.", "Configured product.", "Select round robin.",
            "Round robin is selected.", "New case.", driving), now);

        var caseParentRevisionId = Guid.NewGuid();
        var procedureTcr = new TestChangeReview(project.Id, release.Id, scr.Id,
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware,
                VerificationArtifactKind.Procedure),
            "SRCR-92001.00", now, "HLRTPCR-92001", 0);
        procedureTcr.RecordTestChangeRequired(memberName, now);
        db.Add(procedureTcr);
        var procedureIntroduce = procedureTcr.AddProcedureChange(memberName,
            new TestProcedureChangeDraft("HLRTP-92001", 0, TestProcedureLevel.HighLevel,
                TestProcedureChangeKind.Introduce, "Waypoint procedure", "Run the case.",
                "Configured product.", "Execute the steps.", "The steps pass.", "New procedure.",
                driving, "[]", "",
                VerificationProcedureParentKind.Allocated,
                JsonSerializer.Serialize(new[] { caseParentRevisionId }), "",
                // A software Procedure carries the fuller controlled body the domain demands of it. These are
                // exactly the fields that would be lost by flattening a procedure into a requirement statement.
                EnvironmentSetup: "Bench rig with the configured product.",
                TestData: "Five oceanic waypoints.",
                OrderedSteps: "1. Select round robin. 2. Sequence.",
                ExpectedObservations: "The active mode is annunciated.",
                Cleanup: "Restore fixed order.",
                ToolingAutomation: "Manual."), now);

        await db.SaveChangesAsync();
        return new(project.Id, systemTcr.Id, caseTcr.Id, procedureTcr.Id, introduce.Id, modify.Id, retire.Id,
            caseIntroduce.Id, procedureIntroduce.Id, coveredRevision.Id, droppedRevision.Id,
            caseParentRevisionId, memberName, outsiderName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private static async Task<JsonElement> ContentAsync(HttpClient client, Guid reviewId)
    {
        using var response = await client.GetAsync($"/api/test-change-reviews/{reviewId}/proposal-content");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Item(JsonElement body, Guid id) =>
        body.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == id.ToString());

    [Fact]
    public async Task A_system_procedure_package_is_served_as_verification_content_not_as_a_requirement()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var body = await ContentAsync(client, fixture.SystemTcrId);

        // The discriminator the client switches on, in the vocabulary the trace projection already uses.
        Assert.Equal("TestChangeRequest", body.GetProperty("ownerKind").GetString());
        Assert.Equal("Procedure", body.GetProperty("artifactKind").GetString());
        Assert.Equal("System", body.GetProperty("discipline").GetString());

        var introduce = Item(body, fixture.IntroduceId);
        // Structured procedure fields, not a flattened statement. Losing this structure would show a
        // verification artifact as though it were a requirement.
        var content = introduce.GetProperty("proposedContent");
        Assert.Equal("Round-robin sequencing", content.GetProperty("title").GetString());
        Assert.Equal("Verify round-robin sequencing.", content.GetProperty("objective").GetString());
        Assert.Equal("Configured product.", content.GetProperty("preconditions").GetString());
        Assert.Equal(ProposedSteps, content.GetProperty("steps").GetString());
        Assert.Equal("Sequencing is round-robin.", content.GetProperty("expectedResult").GetString());
    }

    [Fact]
    public async Task An_introduce_has_no_predecessor_and_says_so_with_a_null()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var introduce = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.IntroduceId);

        Assert.Equal(JsonValueKind.Null, introduce.GetProperty("supersededContent").ValueKind);
        Assert.Equal(JsonValueKind.Null, introduce.GetProperty("baseRevisionId").ValueKind);
    }

    [Fact]
    public async Task A_modify_resolves_the_exact_predecessor_revision_rather_than_the_latest()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var modify = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.ModifyId);

        // Revision 1 exists and says something different. Showing it would report a change to steps the author
        // never touched — the same exact-revision discipline the requirement side holds.
        var superseded = modify.GetProperty("supersededContent");
        Assert.Equal(SupersededSteps, superseded.GetProperty("steps").GetString());
        Assert.NotEqual(LatestSteps, superseded.GetProperty("steps").GetString());
        Assert.Equal(0, modify.GetProperty("supersededRevision").GetInt32());
        Assert.Equal(ProposedSteps, modify.GetProperty("proposedContent").GetProperty("steps").GetString());
    }

    [Fact]
    public async Task A_retire_carries_its_predecessor_identity_and_proposes_no_successor_body()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var retire = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.RetireId);

        Assert.Equal("Retire", retire.GetProperty("kind").GetString());
        // A retirement withdraws a procedure rather than restating it, so an empty body would read as a
        // procedure emptied of its steps. Null means absent.
        Assert.Equal(JsonValueKind.Null, retire.GetProperty("proposedContent").ValueKind);
    }

    [Fact]
    public async Task Proposed_coverage_resolves_exact_requirement_revisions_and_stays_a_proposal()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var modify = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.ModifyId);

        var proposed = modify.GetProperty("proposedCoverage").EnumerateArray().ToList();
        var target = Assert.Single(proposed);
        Assert.Equal(fixture.CoveredRevisionId.ToString(), target.GetProperty("revisionId").GetString());
        Assert.Equal("SR-92001.00", target.GetProperty("displayNumber").GetString());
        Assert.Equal("System", target.GetProperty("level").GetString());
        // A package proposes coverage; it has not verified anything until it is approved and materialised.
        Assert.True(target.GetProperty("isProposedCoverage").GetBoolean());
    }

    [Fact]
    public async Task Removed_coverage_is_reported_separately_from_proposed_coverage()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var modify = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.ModifyId);

        // Deliberately dropping predecessor coverage is a truthful part of the change, and folding it into the
        // proposed list would present a removal as an addition.
        var removed = Assert.Single(modify.GetProperty("removedCoverage").EnumerateArray().ToList());
        Assert.Equal(fixture.RemovedRevisionId.ToString(), removed.GetProperty("revisionId").GetString());
        Assert.Equal("SR-92002.00", removed.GetProperty("displayNumber").GetString());

        var proposedIds = modify.GetProperty("proposedCoverage").EnumerateArray()
            .Select(x => x.GetProperty("revisionId").GetString()).ToList();
        Assert.DoesNotContain(fixture.RemovedRevisionId.ToString(), proposedIds);
    }

    [Fact]
    public async Task A_software_case_package_states_its_artifact_kind_rather_than_leaving_it_to_a_prefix()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var body = await ContentAsync(client, fixture.CaseTcrId);

        Assert.Equal("Case", body.GetProperty("artifactKind").GetString());
        Assert.Equal("HighLevelSoftware", body.GetProperty("discipline").GetString());
        Assert.Equal("Case", Item(body, fixture.CaseIntroduceId).GetProperty("artifactKind").GetString());
    }

    [Fact]
    public async Task A_software_procedure_parent_is_a_case_and_is_not_reported_as_requirement_coverage()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var item = Item(await ContentAsync(client, fixture.ProcedureTcrId), fixture.ProcedureIntroduceId);

        Assert.Equal("Allocated", item.GetProperty("parentKind").GetString());
        var parent = Assert.Single(item.GetProperty("exactParents").EnumerateArray().ToList());
        Assert.Equal(fixture.CaseParentRevisionId.ToString(), parent.GetProperty("revisionId").GetString());
        // The exact parent of a software Procedure is a Case revision. Relabelling it as requirement coverage
        // would tell the reader the procedure verifies a requirement it has no recorded relationship with.
        Assert.Equal("Case", parent.GetProperty("kind").GetString());

        var coverageIds = item.GetProperty("proposedCoverage").EnumerateArray()
            .Select(x => x.GetProperty("revisionId").GetString()).ToList();
        Assert.DoesNotContain(fixture.CaseParentRevisionId.ToString(), coverageIds);
    }

    [Fact]
    public async Task Test_proposal_content_is_refused_to_a_caller_outside_the_project()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Outsider);

        using var response = await client.GetAsync(
            $"/api/test-change-reviews/{fixture.SystemTcrId}/proposal-content");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_test_change_review_is_not_found()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        using var response = await client.GetAsync(
            $"/api/test-change-reviews/{Guid.NewGuid()}/proposal-content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_change_request_identifier_is_not_found_here_rather_than_looked_for_elsewhere()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        // The resource says which aggregate it reads. Falling back to SystemChangeRequests would make the path
        // ambiguous about what it returned, and would be a route to content through the wrong authorization.
        var changeRequestId = await ChangeRequestIdAsync(client, fixture);

        using var response = await client.GetAsync(
            $"/api/test-change-reviews/{changeRequestId}/proposal-content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> ChangeRequestIdAsync(HttpClient client, Fixture fixture)
    {
        using var response = await client.GetAsync($"/api/change-requests?projectId={fixture.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var items = body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("items");
        return items.EnumerateArray().First().GetProperty("id").GetGuid();
    }
}
