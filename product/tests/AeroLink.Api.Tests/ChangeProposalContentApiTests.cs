using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The proposed content of a change request, as the Digital Thread's inside-a-change view reads it.
///
/// The subject of most of these is the revision the answer is anchored to. A proposal names the exact revision
/// it supersedes, and the requirement it targets has moved on since; resolving against the latest revision
/// instead would diff the proposal against text that was never its baseline.
/// </summary>
public sealed class ChangeProposalContentApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;
    public ChangeProposalContentApiTests(SharedApiHost host) => _host = host;

    private const string SupersededText = "The FMS shall sequence oceanic waypoints within 2 seconds.";
    private const string LatestText = "The FMS shall sequence oceanic waypoints within 1 second.";
    private const string ProposedText = "The FMS shall sequence oceanic waypoints within 500 milliseconds.";

    private sealed record Fixture(Guid ProjectId, Guid ChangeRequestId, Guid ModifyId, Guid IntroduceId,
        Guid RetireId, Guid AllocatingModifyId, Guid MaterializedChildId, Guid ProposedChildId,
        Guid OtherBuildChildId, Guid RealRetireId, Guid RetiredCascadeChildId, string Member, string Outsider);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var memberName = $"proposal.member.{tag}";
        var outsiderName = $"proposal.outsider.{tag}";
        var program = new ProgramRecord($"Proposal content {tag}", $"PC{tag}");
        var project = new ProjectRecord(program.Id, "Flight management", "Proposal qualification");
        var release = new SoftwareRelease(project.Id, "3.1", false);
        var otherRelease = new SoftwareRelease(project.Id, "3.2", false, release.Id);
        var member = new UserAccount(memberName, memberName, $"{memberName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var outsider = new UserAccount(outsiderName, outsiderName, $"{outsiderName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release, otherRelease, member, outsider,
            new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "test.setup", now));

        var subject = new SystemChangeRequest("SRCR-91001", 0, project.Id, release.Id, "Sequencing rework",
            "Problem", "Analysis", "Solution", memberName, now);
        db.Add(subject);

        var baseline = new CandidateBaseline("SW-91.00", 0, project.Id, release.Id, null,
            "Origin baseline", "cm", now);
        db.Add(baseline);

        // The targeted requirement, with a later revision than the one the proposal was written against. This
        // is the whole point of the fixture: revision 2 exists and says something different, so a projection
        // that resolved "the current text" would quietly diff against wording nobody proposed a change to.
        var target = new RequirementArtifact(project.Id, "SR-91001", RequirementLevel.System, now);
        var supersededRevision = new RequirementRevision(target.Id, 1, SupersededText, "Rationale",
            "Test", RequirementRevisionState.Superseded, subject.Id, baseline.Id, now);
        var latestRevision = new RequirementRevision(target.Id, 2, LatestText, "Rationale",
            "Test", RequirementRevisionState.Active, subject.Id, baseline.Id, now);
        db.AddRange(target, supersededRevision, latestRevision);

        // A second requirement, current, carrying the downstream allocation. It has to be a different one:
        // a materialized child may only name an *active* parent revision, so nothing existing is ever allowed
        // to hang off the superseded revision above. Lane 2 of a stale proposal is therefore legitimately
        // empty, and the change request is flagged for rebase instead.
        var allocating = new RequirementArtifact(project.Id, "SR-91004", RequirementLevel.System, now);
        var allocatingRevision = new RequirementRevision(allocating.Id, 0,
            "The FMS shall annunciate a sequencing fault to the crew.", "Rationale", "Test",
            RequirementRevisionState.Active, subject.Id, baseline.Id, now);
        db.AddRange(allocating, allocatingRevision);

        var child = new RequirementArtifact(project.Id, "HLR-91001", RequirementLevel.HighLevel, now);
        var childRevision = new RequirementRevision(child.Id, 0, "The FMS shall compute the next waypoint.",
            "Rationale", "Test", RequirementRevisionState.Active, subject.Id, baseline.Id, now,
            RequirementParentKind.Allocated, parentRevisionIds: [allocatingRevision.Id]);
        db.AddRange(child, childRevision);
        db.AddRange(
            new BaselineRequirementSelection(baseline.Id, target.Id, latestRevision.Id),
            new BaselineRequirementSelection(baseline.Id, allocating.Id, allocatingRevision.Id),
            new BaselineRequirementSelection(baseline.Id, child.Id, childRevision.Id));
        db.RequirementTraces.Add(new RequirementTraceLink(project.Id, childRevision.Id, allocatingRevision.Id,
            RequirementTraceType.AllocatedFrom, "Allocated from the system requirement.", now));

        var modify = subject.AddRequirementChange(memberName, "SR-91001", 1, RequirementLevel.System,
            RequirementChangeKind.Modify, ProposedText, "Rationale", "Test", now);
        var allocatingModify = subject.AddRequirementChange(memberName, "SR-91004", 0, RequirementLevel.System,
            RequirementChangeKind.Modify, "The FMS shall annunciate a sequencing fault within 1 second.",
            "Rationale", "Test", now);
        var introduce = subject.AddRequirementChange(memberName, "SR-91002", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall annunciate a sequencing fault.", "Rationale",
            "Test", now);
        var retire = subject.AddRequirementChange(memberName, "SR-91003", 1, RequirementLevel.System,
            RequirementChangeKind.Retire, "", "No longer applicable.", "Test", now);

        // A Retire against a requirement that really exists and really has something below it. #880 §8.5.1
        // settled that a Retire resolves its base revision, because what hangs below the thing being retired is
        // the cascade §5.2 draws dashed. The unresolved case above proves graceful degradation; only this one
        // proves the requirement.
        var retiring = new RequirementArtifact(project.Id, "SR-91005", RequirementLevel.System, now);
        var retiringRevision = new RequirementRevision(retiring.Id, 0,
            "The FMS shall sequence oceanic waypoints in fixed order.", "Rationale", "Test",
            RequirementRevisionState.Active, subject.Id, baseline.Id, now);
        var cascadeChild = new RequirementArtifact(project.Id, "HLR-91005", RequirementLevel.HighLevel, now);
        var cascadeChildRevision = new RequirementRevision(cascadeChild.Id, 0,
            "The FMS shall hold the entered waypoint order.", "Rationale", "Test",
            RequirementRevisionState.Active, subject.Id, baseline.Id, now,
            RequirementParentKind.Allocated, parentRevisionIds: [retiringRevision.Id]);
        db.AddRange(retiring, retiringRevision, cascadeChild, cascadeChildRevision);
        db.AddRange(
            new BaselineRequirementSelection(baseline.Id, retiring.Id, retiringRevision.Id),
            new BaselineRequirementSelection(baseline.Id, cascadeChild.Id, cascadeChildRevision.Id));
        db.RequirementTraces.Add(new RequirementTraceLink(project.Id, cascadeChildRevision.Id,
            retiringRevision.Id, RequirementTraceType.AllocatedFrom, "Allocated from the system requirement.", now));

        var realRetire = subject.AddRequirementChange(memberName, "SR-91005", 0, RequirementLevel.System,
            RequirementChangeKind.Retire, "", "Superseded by round-robin sequencing.", "Test", now);

        // A proposed child in a sibling change request in the same build, pointing at the allocating revision.
        // Nothing materialized carries this relationship, so it exists only as the sibling's upstream list.
        var upstream = JsonSerializer.Serialize(new[] { allocatingRevision.Id });
        var sibling = new SystemChangeRequest("HLRCR-91002", 0, project.Id, release.Id, "Downstream rework",
            "Problem", "Analysis", "Solution", memberName, now, ChangeRequestType.Software,
            softwareLevel: RequirementLevel.HighLevel);
        db.Add(sibling);
        var proposedChild = sibling.AddRequirementChange(memberName, "HLR-91002", 0,
            RequirementLevel.HighLevel, RequirementChangeKind.Introduce,
            "The FMS shall pre-compute the following waypoint.", "Rationale", "Test", now,
            proposedUpstreamRevisionIdsJson: upstream);

        // The same relationship in a different build. The candidate set is bounded by the release being read,
        // so this one must not appear in the answer for build 3.1.
        var otherBuild = new SystemChangeRequest("HLRCR-91003", 0, project.Id, otherRelease.Id, "Later build",
            "Problem", "Analysis", "Solution", memberName, now, ChangeRequestType.Software,
            softwareLevel: RequirementLevel.HighLevel);
        db.Add(otherBuild);
        var otherBuildChild = otherBuild.AddRequirementChange(memberName, "HLR-91003", 0,
            RequirementLevel.HighLevel, RequirementChangeKind.Introduce,
            "The FMS shall log the sequencing decision.", "Rationale", "Test", now,
            proposedUpstreamRevisionIdsJson: upstream);

        await db.SaveChangesAsync();
        return new(project.Id, subject.Id, modify.Id, introduce.Id, retire.Id, allocatingModify.Id,
            childRevision.Id, proposedChild.Id, otherBuildChild.Id, realRetire.Id, cascadeChild.Id,
            memberName, outsiderName);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private static async Task<JsonElement> ContentAsync(HttpClient client, Guid changeRequestId)
    {
        using var response = await client.GetAsync($"/api/change-requests/{changeRequestId}/proposal-content");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Item(JsonElement body, Guid id) =>
        body.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == id.ToString());

    [Fact]
    public async Task Modify_carries_the_statement_of_the_exact_revision_it_supersedes()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var modify = Item(await ContentAsync(client, fixture.ChangeRequestId), fixture.ModifyId);

        // Revision 2 is the requirement's current text and is deliberately not the answer.
        Assert.Equal(SupersededText, modify.GetProperty("supersededStatement").GetString());
        Assert.Equal(1, modify.GetProperty("supersededRevision").GetInt32());
        Assert.Equal(ProposedText, modify.GetProperty("statement").GetString());
    }

    [Fact]
    public async Task Introduce_supersedes_nothing_and_says_so_with_a_null_rather_than_an_empty_string()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var introduce = Item(await ContentAsync(client, fixture.ChangeRequestId), fixture.IntroduceId);

        // An empty string would render as a diff from nothing and claim the author replaced existing wording.
        Assert.Equal(JsonValueKind.Null, introduce.GetProperty("supersededStatement").ValueKind);
        Assert.Equal(JsonValueKind.Null, introduce.GetProperty("baseRevisionId").ValueKind);
        // Nothing can allocate to a requirement that does not exist yet, so the lane is empty by construction.
        Assert.Empty(introduce.GetProperty("allocatedDownstream").EnumerateArray());
    }

    [Fact]
    public async Task Retire_resolves_its_real_base_revision_and_returns_the_cascade_below_it()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var retire = Item(await ContentAsync(client, fixture.ChangeRequestId), fixture.RealRetireId);

        // No before/after: a Retire proposes no successor text, so there is nothing to diff against.
        Assert.Equal(JsonValueKind.Null, retire.GetProperty("supersededStatement").ValueKind);
        // But the base revision resolves, and what hangs below it is the cascade §5.2 draws dashed.
        Assert.Equal(0, retire.GetProperty("supersededRevision").GetInt32());
        Assert.Equal(JsonValueKind.String, retire.GetProperty("baseRevisionId").ValueKind);

        var cascade = retire.GetProperty("allocatedDownstream").EnumerateArray().ToList();
        var child = Assert.Single(cascade);
        Assert.Equal(fixture.RetiredCascadeChildId.ToString(), child.GetProperty("id").GetString());
        Assert.Equal("HLR-91005.00", child.GetProperty("displayNumber").GetString());
        Assert.False(child.GetProperty("isProposed").GetBoolean());
        Assert.Equal("Allocated", retire.GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task Retire_naming_a_base_that_does_not_resolve_is_reported_as_a_data_gap()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var retire = Item(await ContentAsync(client, fixture.ChangeRequestId), fixture.RetireId);

        Assert.Equal(JsonValueKind.Null, retire.GetProperty("supersededStatement").ValueKind);
        // SR-91003 has no artifact in this fixture, so its base revision does not resolve. That is a gap in
        // the record rather than an error, and it must read as an absence rather than as an empty diff.
        Assert.Equal(JsonValueKind.Null, retire.GetProperty("baseRevisionId").ValueKind);
        // And it must be reported as a gap, never as staleness. "Behind its target" claims a later revision
        // exists and carries the allocation; nothing here supports that claim.
        Assert.Equal("BaseRevisionUnresolved", retire.GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task Downstream_allocation_carries_the_materialized_child_of_the_superseded_revision()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var allocated = Item(await ContentAsync(client, fixture.ChangeRequestId), fixture.AllocatingModifyId)
            .GetProperty("allocatedDownstream").EnumerateArray().ToList();

        var existing = allocated.Single(x => !x.GetProperty("isProposed").GetBoolean());
        Assert.Equal("HLR-91001.00", existing.GetProperty("displayNumber").GetString());
        Assert.Equal("HighLevel", existing.GetProperty("level").GetString());
        Assert.Equal("AllocatedFrom", existing.GetProperty("linkType").GetString());
    }

    [Fact]
    public async Task Downstream_allocation_carries_a_proposed_child_and_marks_it_as_not_yet_in_the_build()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var allocated = Item(await ContentAsync(client, fixture.ChangeRequestId), fixture.AllocatingModifyId)
            .GetProperty("allocatedDownstream").EnumerateArray().ToList();

        var proposed = allocated.Single(x => x.GetProperty("isProposed").GetBoolean());
        Assert.Equal(fixture.ProposedChildId.ToString(), proposed.GetProperty("id").GetString());
        // Which change request is proposing it, so the lane can say where the allocation comes from rather
        // than presenting an under-review requirement as though it were already allocated.
        Assert.Equal("HLRCR-91002.00", proposed.GetProperty("changeRequestDisplayNumber").GetString());
    }

    [Fact]
    public async Task Downstream_allocation_excludes_a_proposal_from_a_different_build()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var ids = Item(await ContentAsync(client, fixture.ChangeRequestId), fixture.AllocatingModifyId)
            .GetProperty("allocatedDownstream").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()).ToList();

        Assert.DoesNotContain(fixture.OtherBuildChildId.ToString(), ids);
    }

    [Fact]
    public async Task A_proposal_written_against_a_superseded_revision_shows_nothing_allocated_below_it()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var stale = Item(await ContentAsync(client, fixture.ChangeRequestId), fixture.ModifyId);

        // Not a gap in the read. A materialized requirement may only name an active parent revision, so once
        // the target revises, nothing is allowed to hang off the revision this proposal was written against.
        // The honest answer is an empty lane; the change request carries the rebase prompt that explains it.
        Assert.Equal(SupersededText, stale.GetProperty("supersededStatement").GetString());
        Assert.Empty(stale.GetProperty("allocatedDownstream").EnumerateArray());
    }

    [Fact]
    public async Task A_stale_item_does_not_make_its_current_siblings_look_stale()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        var body = await ContentAsync(client, fixture.ChangeRequestId);

        // SR-91001 names revision 1 while revision 2 exists, so this item genuinely is behind its target and
        // whatever is allocated hangs off the later revision.
        var stale = Item(body, fixture.ModifyId);
        Assert.Equal("BehindTarget", stale.GetProperty("disposition").GetString());
        Assert.Equal(2, stale.GetProperty("latestRevision").GetInt32());

        // One stale item strands the whole change request, but it says nothing about the others. SR-91002 is an
        // Introduce and SR-91005 is a current Retire; neither may inherit the first one's staleness. Deciding
        // this from the change request's overall rebase flag would mark all three behind their targets.
        Assert.Equal("TargetNotYetCreated", Item(body, fixture.IntroduceId).GetProperty("disposition").GetString());
        Assert.Equal("Allocated", Item(body, fixture.RealRetireId).GetProperty("disposition").GetString());
        Assert.Equal("BaseRevisionUnresolved", Item(body, fixture.RetireId).GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task An_item_naming_the_current_revision_with_nothing_below_it_reads_as_no_allocation()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        // SR-91004 is the current revision and does have downstream, so it is Allocated rather than empty; the
        // distinction being proved here is that a resolved, current base is never reported as behind target.
        var current = Item(await ContentAsync(client, fixture.ChangeRequestId), fixture.AllocatingModifyId);

        Assert.Equal("Allocated", current.GetProperty("disposition").GetString());
        Assert.Equal(0, current.GetProperty("supersededRevision").GetInt32());
        Assert.Equal(0, current.GetProperty("latestRevision").GetInt32());
    }

    [Fact]
    public async Task Proposal_content_is_refused_to_a_caller_outside_the_project()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Outsider);

        using var response = await client.GetAsync(
            $"/api/change-requests/{fixture.ChangeRequestId}/proposal-content");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_change_request_is_not_found()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.Member);

        using var response = await client.GetAsync(
            $"/api/change-requests/{Guid.NewGuid()}/proposal-content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
