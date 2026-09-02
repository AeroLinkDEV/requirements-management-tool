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
        Guid RetainedAId, Guid RetainedBId, Guid RemovedCId, Guid AddedDId,
        Guid CaseParentRevisionId, Guid RetiredPredecessorId, Guid MissingReferenceId, Guid PredecessorExecutionId,
        Guid MalformedParentItemId, Guid MalformedCoverageItemId, Guid ForeignItemId, Guid ForeignRevisionId,
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

        // Four requirement revisions, so the coverage arithmetic can actually be observed: the Modify keeps
        // A and B, drops C and adds D, leaving the successor covering A, B and D.
        RequirementRevision Req(string number, string statement, out RequirementArtifact artifact)
        {
            artifact = new RequirementArtifact(project.Id, number, RequirementLevel.System, now);
            var revision = new RequirementRevision(artifact.Id, 0, statement, "Rationale", "Test",
                RequirementRevisionState.Active, scr.Id, baseline.Id, now);
            db.AddRange(artifact, revision);
            return revision;
        }

        var retainedA = Req("SR-92001", "The FMS shall sequence oceanic waypoints.", out _);
        var retainedB = Req("SR-92002", "The FMS shall annunciate the sequencing mode.", out _);
        var removedC = Req("SR-92003", "The FMS shall sequence in fixed order.", out _);
        var addedD = Req("SR-92004", "The FMS shall sequence in round-robin order.", out _);

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

        // A real controlled predecessor for the Retire, so its exact resolution is actually qualified rather
        // than passing because nothing was asserted about it.
        var retiring = new TestProcedure(project.Id, "SYSTP-92003", "Fixed-order sequencing", memberName, now,
            TestProcedureLevel.System, null, VerificationArtifactKind.Procedure);
        var retiringRevision = new TestProcedureRevision(retiring.Id, 0, "Verify fixed order",
            "Configured product.", "Enter waypoints in order.", "The order holds.",
            TestProcedureState.Approved, memberName, now);
        db.AddRange(retiring, retiringRevision);

        // A real controlled software Case, to be the software Procedure's exact parent. A random GUID would
        // only have proven the projection echoes an identifier back with a label it chose itself.
        var parentCase = new TestProcedure(project.Id, "HLRTC-92010", "Round-robin case", memberName, now,
            TestProcedureLevel.HighLevel, null, VerificationArtifactKind.Case);
        var parentCaseRevision = new TestProcedureRevision(parentCase.Id, 0, "Cover round robin",
            "Configured product.", "Select round robin.", "Round robin is selected.",
            TestProcedureState.Approved, memberName, now);
        db.AddRange(parentCase, parentCaseRevision);

        // An identity the record names that nothing in this Project answers to.
        var missingReferenceId = Guid.NewGuid();

        // The full successor selection is the exact-parent list; driving and removed are the deltas.
        var finalSelection = JsonSerializer.Serialize(new[] { retainedA.Id, retainedB.Id, addedD.Id });
        var driving = JsonSerializer.Serialize(new[] { addedD.Id });
        var removed = JsonSerializer.Serialize(new[] { removedC.Id });

        // A System Procedure package: Introduce, Modify against the exact earlier revision, and Retire.
        var systemTcr = new TestChangeReview(project.Id, release.Id, scr.Id,
            new VerificationArtifactKey(VerificationDiscipline.System, VerificationArtifactKind.Procedure),
            "SRCR-92001.00", now, "SYSTPCR-92001", 0);
        systemTcr.RecordTestChangeRequired(memberName, now);
        db.Add(systemTcr);
        // Introduce: its exact parents are its initial requirement coverage, plus one identity that does not
        // resolve, so the unresolved-reference behaviour is exercised on a real response.
        var introduce = systemTcr.AddProcedureChange(memberName, new TestProcedureChangeDraft("SYSTP-92002", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Introduce, "Round-robin sequencing",
            "Verify round-robin sequencing.", "Configured product.", ProposedSteps,
            "Sequencing is round-robin.", "New coverage.",
            JsonSerializer.Serialize(new[] { addedD.Id }), "[]", "",
            VerificationProcedureParentKind.Allocated,
            JsonSerializer.Serialize(new[] { addedD.Id, missingReferenceId })), now);

        // Modify: retains A and B, removes C, adds D. The successor covers A, B and D.
        var modify = systemTcr.AddProcedureChange(memberName, new TestProcedureChangeDraft("SYSTP-92001", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Modify, "Oceanic sequencing",
            "Verify sequencing.", "Configured product.", ProposedSteps, "The sequence is correct.",
            "Reworked for round-robin.", driving, removed, "",
            VerificationProcedureParentKind.Allocated, finalSelection), now);

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
            "Round robin is selected.", "New case.", driving, "[]", "",
            VerificationProcedureParentKind.Allocated,
            JsonSerializer.Serialize(new[] { addedD.Id })), now);

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
                JsonSerializer.Serialize(new[] { parentCaseRevision.Id }), "",
                // A software Procedure carries the fuller controlled body the domain demands of it. These are
                // exactly the fields that would be lost by flattening a procedure into a requirement statement.
                EnvironmentSetup: "Bench rig with the configured product.",
                TestData: "Five oceanic waypoints.",
                OrderedSteps: "1. Select round robin. 2. Sequence.",
                ExpectedObservations: "The active mode is annunciated.",
                Cleanup: "Restore fixed order.",
                ToolingAutomation: "Manual."), now);

        // A real revision in a DIFFERENT Project, recorded by a proposal in this one. The projection is
        // Project-scoped, so it must resolve to nothing here — and must not carry a single detail of it back.
        var foreignProgram = new ProgramRecord($"Foreign {tag}", $"FN{tag}");
        var foreignProject = new ProjectRecord(foreignProgram.Id, "Other product", "Foreign");
        var foreignRelease = new SoftwareRelease(foreignProject.Id, "9.9", false);
        var foreignBaseline = new CandidateBaseline("SW-99.00", 0, foreignProject.Id, foreignRelease.Id, null,
            "Foreign", "cm", now);
        var foreignScr = new SystemChangeRequest("SRCR-99001", 0, foreignProject.Id, foreignRelease.Id,
            "Foreign change", "Problem", "Analysis", "Solution", memberName, now);
        var foreignArtifact = new RequirementArtifact(foreignProject.Id, "SR-99001",
            RequirementLevel.System, now);
        var foreignRevision = new RequirementRevision(foreignArtifact.Id, 0,
            "The other product shall do something entirely unrelated.", "Rationale", "Test",
            RequirementRevisionState.Active, foreignScr.Id, foreignBaseline.Id, now);
        db.AddRange(foreignProgram, foreignProject, foreignRelease, foreignBaseline, foreignScr,
            foreignArtifact, foreignRevision);

        var foreignItem = systemTcr.AddProcedureChange(memberName, new TestProcedureChangeDraft("SYSTP-92004", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Introduce, "Foreign reference",
            "Verify nothing leaks.", "Configured product.", "Observe.", "Nothing leaks.", "Non-leak.",
            "[]", "[]", "", VerificationProcedureParentKind.Allocated,
            JsonSerializer.Serialize(new[] { foreignRevision.Id })), now);

        // Two proposals whose stored lists are then made unreadable through the persistence seam, which is how
        // a Draft checked in before submission validation can legitimately look.
        var malformedParent = systemTcr.AddProcedureChange(memberName,
            new TestProcedureChangeDraft("SYSTP-92005", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "Malformed parents", "Verify parsing.",
                "Configured product.", "Observe.", "Parsed.", "Malformed.", "[]", "[]", "",
                VerificationProcedureParentKind.Allocated, "[]"), now);
        var malformedCoverage = systemTcr.AddProcedureChange(memberName,
            new TestProcedureChangeDraft("SYSTP-92006", 0, TestProcedureLevel.System,
                TestProcedureChangeKind.Introduce, "Malformed coverage", "Verify parsing.",
                "Configured product.", "Observe.", "Parsed.", "Malformed."), now);

        // One run of the exact predecessor revision, and one of a later revision of the same procedure. Only
        // the first is evidence for the proposal that names revision 0.
        var predecessorExecution = new TestExecution(project.Id, supersededRevision.Id, null, null,
            TestOutcome.Pass, memberName, "Bench", "Sequencing behaved as specified.", "evidence://run-0",
            now, now, release.Id);
        var laterExecution = new TestExecution(project.Id, latestRevision.Id, null, null,
            TestOutcome.Fail, memberName, "Bench", "Later revision failed.", "evidence://run-1", now, now,
            release.Id);
        db.AddRange(predecessorExecution, laterExecution);

        await db.SaveChangesAsync();

        // Written through EF rather than the aggregate, because the aggregate is right to refuse it. Production
        // validation is untouched; this reproduces a row that controlled editing can already leave behind.
        db.Entry(malformedParent).Property("ParentRevisionIdsJson").CurrentValue = "{ not a list";
        db.Entry(malformedCoverage).Property("DrivingRequirementRevisionIdsJson").CurrentValue = "oops";
        await db.SaveChangesAsync();

        return new(project.Id, systemTcr.Id, caseTcr.Id, procedureTcr.Id, introduce.Id, modify.Id, retire.Id,
            caseIntroduce.Id, procedureIntroduce.Id, retainedA.Id, retainedB.Id, removedC.Id, addedD.Id,
            parentCaseRevision.Id, retiringRevision.Id, missingReferenceId,
            predecessorExecution.Id, malformedParent.Id, malformedCoverage.Id, foreignItem.Id, foreignRevision.Id,
            memberName, outsiderName);
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
    public async Task A_retire_resolves_its_real_predecessor_and_proposes_no_successor_body()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var retire = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.RetireId);

        Assert.Equal("Retire", retire.GetProperty("kind").GetString());
        // A retirement withdraws a procedure rather than restating it, so an empty body would read as a
        // procedure emptied of its steps. Null means absent.
        Assert.Equal(JsonValueKind.Null, retire.GetProperty("proposedContent").ValueKind);

        // The predecessor is a real controlled revision, resolved exactly. Asserting only that the kind is
        // Retire would pass against a base number nothing answers to, which is how the requirement-side Retire
        // test previously proved nothing.
        Assert.Equal(fixture.RetiredPredecessorId.ToString(), retire.GetProperty("baseRevisionId").GetString());
        Assert.Equal(0, retire.GetProperty("supersededRevision").GetInt32());
        // The predecessor body travels as factual context, not as half of a diff: a Retire has no successor
        // text, so 4B must not render it as a before/after.
        Assert.Equal("Enter waypoints in order.",
            retire.GetProperty("supersededContent").GetProperty("steps").GetString());

        // A retirement proposes no successor coverage.
        Assert.Empty(retire.GetProperty("finalCoverage").EnumerateArray());
    }

    [Fact]
    public async Task A_modify_reports_the_full_successor_coverage_not_only_what_it_added()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var modify = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.ModifyId);

        static IReadOnlyList<string> Ids(JsonElement element, string property) =>
            element.GetProperty(property).EnumerateArray()
                .Select(x => x.GetProperty("revisionId").GetString()!).OrderBy(x => x).ToList();

        // The successor keeps A and B, drops C and gains D. A lane fed only the added delta would show D alone
        // and tell the reader that A and B had stopped being covered.
        Assert.Equal(
            new[] { fixture.RetainedAId.ToString(), fixture.RetainedBId.ToString(), fixture.AddedDId.ToString() }
                .OrderBy(x => x).ToList(),
            Ids(modify, "finalCoverage"));

        Assert.Equal(new[] { fixture.AddedDId.ToString() }, Ids(modify, "addedCoverage"));
        Assert.Equal(new[] { fixture.RemovedCId.ToString() }, Ids(modify, "removedCoverage"));

        // The removed requirement is not in the successor set.
        Assert.DoesNotContain(fixture.RemovedCId.ToString(), Ids(modify, "finalCoverage"));
    }

    [Fact]
    public async Task A_removed_requirement_never_describes_itself_as_proposed_coverage()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var modify = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.ModifyId);
        var removed = Assert.Single(modify.GetProperty("removedCoverage").EnumerateArray().ToList());

        // The list a target sits in states its meaning. A single shared boolean previously said
        // isProposedCoverage: true on rows that were being removed — a removal claiming to be proposed coverage.
        Assert.False(removed.TryGetProperty("isProposedCoverage", out _),
            "A coverage target must not carry a flag whose meaning breaks for removal.");
        Assert.Equal(fixture.RemovedCId.ToString(), removed.GetProperty("revisionId").GetString());
        Assert.Equal("SR-92003.00", removed.GetProperty("displayNumber").GetString());
    }

    [Fact]
    public async Task A_system_procedure_parent_is_a_requirement_not_a_case()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var modify = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.ModifyId);

        // A System Procedure takes requirement revisions as exact parents; only a *software* Procedure takes
        // Case revisions. Deciding the kind from the package being a Procedure reports this one as hanging off
        // a Case it has no relationship with.
        var parents = modify.GetProperty("exactParents").EnumerateArray().ToList();
        Assert.NotEmpty(parents);
        Assert.All(parents, parent => Assert.Equal("Requirement", parent.GetProperty("kind").GetString()));

        var retained = parents.Single(x => x.GetProperty("revisionId").GetString() == fixture.RetainedAId.ToString());
        Assert.True(retained.GetProperty("resolved").GetBoolean());
        Assert.Equal("SR-92001.00", retained.GetProperty("displayNumber").GetString());
        Assert.Equal("System", retained.GetProperty("level").GetString());
    }

    [Fact]
    public async Task An_unresolvable_recorded_reference_is_reported_as_a_gap_rather_than_dropped()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var introduce = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.IntroduceId);

        // The proposal names two exact parents; one resolves and one does not. Dropping the second would show a
        // smaller relationship set than the record holds, which on a traceability surface reads as "nothing is
        // recorded" rather than "something is recorded that cannot be resolved".
        var parents = introduce.GetProperty("exactParents").EnumerateArray().ToList();
        Assert.Equal(2, parents.Count);

        var unresolved = parents.Single(x => !x.GetProperty("resolved").GetBoolean());
        Assert.Equal(fixture.MissingReferenceId.ToString(), unresolved.GetProperty("revisionId").GetString());
        // No kind, because nothing located it and so nothing establishes what it is. What the package expected
        // to find is a different claim, and it lives on the gap where it reads as an expectation.
        Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("kind").ValueKind);
        Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("displayNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("level").ValueKind);
        Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("artifactId").ValueKind);

        var gap = Assert.Single(introduce.GetProperty("referenceGaps").EnumerateArray().ToList());
        Assert.Equal(fixture.MissingReferenceId.ToString(), gap.GetProperty("revisionId").GetString());
        Assert.Equal("ExactParent", gap.GetProperty("role").GetString());
        Assert.Equal("Requirement", gap.GetProperty("expectedKind").GetString());
        Assert.Equal("UnresolvedReference", gap.GetProperty("reason").GetString());

        // The one that does resolve is unaffected.
        Assert.Contains(parents, x => x.GetProperty("revisionId").GetString() == fixture.AddedDId.ToString()
            && x.GetProperty("resolved").GetBoolean());
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

        // A real controlled Case revision, resolved to its actual identity. Echoing back a random identifier
        // with a label the projection chose itself would prove only that it can repeat a GUID.
        Assert.Equal(fixture.CaseParentRevisionId.ToString(), parent.GetProperty("revisionId").GetString());
        Assert.Equal("Case", parent.GetProperty("kind").GetString());
        Assert.True(parent.GetProperty("resolved").GetBoolean());
        Assert.Equal("HLRTC-92010.00", parent.GetProperty("displayNumber").GetString());
        Assert.Equal("HighLevel", parent.GetProperty("level").GetString());

        // A Case parent is not requirement coverage, so it must not appear in the coverage lists.
        var coverageIds = item.GetProperty("finalCoverage").EnumerateArray()
            .Select(x => x.GetProperty("revisionId").GetString()).ToList();
        Assert.DoesNotContain(fixture.CaseParentRevisionId.ToString(), coverageIds);
    }

    [Fact]
    public async Task A_malformed_exact_parent_list_is_a_stated_gap_rather_than_an_empty_relationship_set()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        // The rest of the proposal still reads: one unreadable list must not fail the whole Digital Thread.
        var body = await ContentAsync(client, fixture.SystemTcrId);
        var item = Item(body, fixture.MalformedParentItemId);

        // "No relationships were recorded" and "relationship data exists that cannot be interpreted" are
        // different facts, and reporting the second as the first asserts an absence nobody established.
        Assert.Empty(item.GetProperty("exactParents").EnumerateArray());
        var gap = Assert.Single(item.GetProperty("referenceGaps").EnumerateArray().ToList());
        Assert.Equal("MalformedReferenceList", gap.GetProperty("reason").GetString());
        Assert.Equal("ExactParent", gap.GetProperty("role").GetString());
        // No identity can be named, because the bytes could not be read as one.
        Assert.Equal(JsonValueKind.Null, gap.GetProperty("revisionId").ValueKind);
    }

    [Fact]
    public async Task A_malformed_coverage_list_is_reported_against_its_own_role()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var item = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.MalformedCoverageItemId);

        Assert.Empty(item.GetProperty("addedCoverage").EnumerateArray());
        var gap = Assert.Single(item.GetProperty("referenceGaps").EnumerateArray().ToList());
        Assert.Equal("MalformedReferenceList", gap.GetProperty("reason").GetString());
        // The role says which relationship is unreadable, so the reader is not left guessing which lane lied.
        Assert.Equal("AddedCoverage", gap.GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_reference_to_another_project_resolves_to_nothing_and_leaks_nothing()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var item = Item(await ContentAsync(client, fixture.SystemTcrId), fixture.ForeignItemId);

        var parent = Assert.Single(item.GetProperty("exactParents").EnumerateArray().ToList());
        Assert.Equal(fixture.ForeignRevisionId.ToString(), parent.GetProperty("revisionId").GetString());
        Assert.False(parent.GetProperty("resolved").GetBoolean());
        // Not a single detail of the other Project's record crosses this seam, and no kind is claimed for it.
        Assert.Equal(JsonValueKind.Null, parent.GetProperty("kind").ValueKind);
        Assert.Equal(JsonValueKind.Null, parent.GetProperty("displayNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, parent.GetProperty("level").ValueKind);
        Assert.Equal(JsonValueKind.Null, parent.GetProperty("artifactId").ValueKind);

        // The gap stays visible: the record names something, and the reader must not be shown a smaller
        // relationship set than the record holds.
        var gap = Assert.Single(item.GetProperty("referenceGaps").EnumerateArray().ToList());
        Assert.Equal("UnresolvedReference", gap.GetProperty("reason").GetString());
        Assert.Equal("Requirement", gap.GetProperty("expectedKind").GetString());

        // Nothing anywhere in the response carries the foreign identifier or its statement.
        var raw = body(item);
        Assert.DoesNotContain("SR-99001", raw);
        Assert.DoesNotContain("entirely unrelated", raw);
    }

    private static string body(JsonElement element) => element.GetRawText();

    [Fact]
    public async Task Executions_are_only_those_of_the_exact_predecessor_revision()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var executions = (await ContentAsync(client, fixture.SystemTcrId))
            .GetProperty("executions").EnumerateArray().ToList();

        // The Modify names revision 0. Revision 1 of the same procedure has its own run, and it must not be
        // offered as evidence for the revision this proposal actually changes.
        Assert.Single(executions);
        Assert.Equal(fixture.PredecessorExecutionId.ToString(), executions[0].GetProperty("id").GetString());
        Assert.Equal("Pass", executions[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Build_effect_is_empty_when_no_baseline_selected_this_package()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        // Same rule as the requirement side: a candidate existing for the release is not this package being
        // selected into it.
        Assert.Empty((await ContentAsync(client, fixture.SystemTcrId)).GetProperty("buildEffect").EnumerateArray());
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
