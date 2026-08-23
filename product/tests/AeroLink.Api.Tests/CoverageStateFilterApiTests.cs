using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The requirements workspace could filter on the verification *method* an author declared, which says what
/// kind of evidence is intended and nothing whatever about whether any exists. "Which requirements are
/// uncovered?" — the question a verification engineer actually arrives with — had no answer short of the
/// release-readiness counts, which appear far too late to act on.
///
/// These tests pin the three states through the endpoint rather than against the projection, because a
/// definition that is correct in a service and unreachable from HTTP is a capability this product does not
/// have. They also pin the state that a naive implementation gets wrong: coverage that is not suspect, but
/// names a procedure revision nobody has approved.
/// </summary>
public sealed class CoverageStateFilterApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public CoverageStateFilterApiTests(SharedApiHost host)
    {
        _host = host;
    }

    private sealed record Fixture(Guid ProjectId, Guid OtherProjectId, string MemberName, string OutsiderName);

    /// <summary>
    /// Four requirements, one in each condition that matters:
    ///
    /// HLR-00000801 settled coverage — an approved procedure revision with nothing in flight behind it.
    /// HLR-00000802 suspect coverage — carried forward across a change and never reconfirmed.
    /// HLR-00000803 no coverage link at all.
    /// HLR-00000804 coverage that is *not* suspect but names a Draft procedure revision. Nobody approved it,
    ///               so it cannot settle anything, and an implementation testing only IsSuspect calls this
    ///               Covered while the release gate calls it uncovered.
    /// </summary>
    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        // Unique per test: user accounts and Program codes are globally unique-constrained, so a shared
        // host/database requires per-test identities. Requirement/procedure numbers are project-scoped and stay fixed.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var member = $"coverage.reader.{tag}";
        var outsider = $"coverage.outsider.{tag}";

        var program = new ProgramRecord($"Coverage Program {tag}", $"CVP{tag}");
        var project = new ProjectRecord(program.Id, "Software", "Coverage Software");
        var otherProgram = new ProgramRecord($"Foreign Program {tag}", $"FGN{tag}");
        var otherProject = new ProjectRecord(otherProgram.Id, "Software", "Foreign Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var scr = new SystemChangeRequest("SRCR-00800", 0, project.Id, release.Id, "Coverage", "P", "A", "S", "author", now);
        var baseline = new CandidateBaseline("SW-80.00", 0, project.Id, release.Id, null, "Candidate", "cm", now);
        db.AddRange(program, project, otherProgram, otherProject, release, scr, baseline);

        // Every revision is provenanced to a real change request and a real baseline, because both are
        // enforced foreign keys — a revision that came from nowhere is not a state this product allows.
        RequirementRevision Revision(RequirementArtifact artifact, string statement) =>
            new(artifact.Id, 1, statement, "Rationale", "Test", RequirementRevisionState.Active,
                scr.Id, baseline.Id, now, RequirementParentKind.Derived,
                "This coverage-state fixture intentionally keeps each requirement standalone.");

        var settled = new RequirementArtifact(project.Id, "HLR-00000801", RequirementLevel.HighLevel, now);
        var suspect = new RequirementArtifact(project.Id, "HLR-00000802", RequirementLevel.HighLevel, now);
        var bare = new RequirementArtifact(project.Id, "HLR-00000803", RequirementLevel.HighLevel, now);
        var unapproved = new RequirementArtifact(project.Id, "HLR-00000804", RequirementLevel.HighLevel, now);
        var settledRevision = Revision(settled, "The FMS shall sequence oceanic waypoints.");
        var suspectRevision = Revision(suspect, "The FMS shall advance on the configured trigger.");
        var bareRevision = Revision(bare, "The FMS shall record oceanic entry time.");
        var unapprovedRevision = Revision(unapproved, "The FMS shall annunciate a sequencing failure.");
        db.AddRange(settled, suspect, bare, unapproved,
            settledRevision, suspectRevision, bareRevision, unapprovedRevision);

        var approvedProcedure = new TestProcedure(project.Id, "HLRTC-00000801", "Oceanic sequencing", "test.author", now);
        // Approved as materialisation writes it, on the authority of the package that carried the change.
        var approvedRevision = new TestProcedureRevision(approvedProcedure.Id, 1, "Objective", "Preconditions",
            "Steps", "Expected", TestProcedureState.Approved, "test.author", now);

        var draftProcedure = new TestProcedure(project.Id, "HLRTC-00000802", "Failure annunciation", "test.author", now);
        var draftRevision = new TestProcedureRevision(draftProcedure.Id, 1, "Objective", "Preconditions",
            "Steps", "Expected", TestProcedureState.Draft, "test.author", now);

        db.AddRange(approvedProcedure, approvedRevision, draftProcedure, draftRevision);
        db.TestCoverage.Add(new TestRequirementCoverage(approvedRevision.Id, settledRevision.Id));
        db.TestCoverage.Add(TestRequirementCoverage.CarriedForward(approvedRevision.Id, suspectRevision.Id,
            "Carried across a wording change and never reconfirmed.", now));
        db.TestCoverage.Add(new TestRequirementCoverage(draftRevision.Id, unapprovedRevision.Id));

        foreach (var (user, program_, role) in new[]
                 {
                     (member, program, ProgramRole.Engineer),
                     (outsider, otherProgram, ProgramRole.Engineer)
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program_.Id, role, "test.setup", now));
        }

        await db.SaveChangesAsync();
        return new Fixture(project.Id, otherProject.Id, member, outsider);
    }

    private static async Task SignInAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private static async Task<JsonElement> WorkspaceAsync(HttpClient client, Guid projectId, string query = "")
    {
        using var response = await client.GetAsync(
            $"/api/enterprise-requirements/workspace?projectId={projectId}&page=1&pageSize=50{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Numbers(JsonElement workspace) =>
        [.. workspace.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("baseNumber").GetString()!)];

    [Fact]
    public async Task Each_coverage_state_returns_exactly_the_requirements_in_it()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.MemberName);

        Assert.Equal(["HLR-00000801"], Numbers(await WorkspaceAsync(client, fixture.ProjectId, "&coverageState=covered")));
        Assert.Equal(["HLR-00000802", "HLR-00000804"], Numbers(await WorkspaceAsync(client, fixture.ProjectId, "&coverageState=suspect")));
        Assert.Equal(["HLR-00000803"], Numbers(await WorkspaceAsync(client, fixture.ProjectId, "&coverageState=uncovered")));

        // Exhaustive and mutually exclusive: every requirement lands in exactly one state.
        var all = await WorkspaceAsync(client, fixture.ProjectId);
        Assert.Equal(4, all.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// The case the release gate already got right and a coverage filter can easily get wrong. The link on
    /// HLR-00000804 is not suspect, so <c>!IsSuspect</c> alone reports it covered — while the procedure
    /// revision it names has never been approved and the gate refuses to count it.
    /// </summary>
    [Fact]
    public async Task Coverage_naming_an_unapproved_procedure_revision_is_not_covered()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.MemberName);

        var covered = Numbers(await WorkspaceAsync(client, fixture.ProjectId, "&coverageState=covered"));
        Assert.DoesNotContain("HLR-00000804", covered);

        var row = (await WorkspaceAsync(client, fixture.ProjectId, "&search=HLR-00000804"))
            .GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Suspect", row.GetProperty("coverageState").GetString());
    }

    /// <summary>
    /// The workspace and the release readiness gate must not answer "is this covered?" two ways. Both now
    /// read one predicate; this asserts the agreement rather than the sharing, so a future divergence fails
    /// here even if somebody reintroduces a second implementation.
    /// </summary>
    [Fact]
    public async Task The_workspace_filter_and_the_release_gate_agree_on_what_is_covered()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.MemberName);

        var covered = Numbers(await WorkspaceAsync(client, fixture.ProjectId, "&coverageState=covered"));

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var revisionIds = await db.RequirementRevisions.AsNoTracking()
            .Join(db.Requirements.AsNoTracking().Where(a => a.ProjectId == fixture.ProjectId),
                r => r.ArtifactId, a => a.Id, (r, a) => new { r.Id, a.BaseNumber })
            .ToListAsync();
        var settledIds = await VerificationCoverageProjection.SettledCoveredAsync(
            db, [.. revisionIds.Select(x => x.Id)], default);
        var gateCovered = revisionIds.Where(x => settledIds.Contains(x.Id))
            .Select(x => x.BaseNumber).OrderBy(x => x).ToArray();

        Assert.Equal(gateCovered, covered);
    }

    [Fact]
    public async Task Coverage_filters_compose_with_level_search_and_paging()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.MemberName);

        // Level narrows the suspect set to the software requirements in it.
        Assert.Equal(["HLR-00000802", "HLR-00000804"],
            Numbers(await WorkspaceAsync(client, fixture.ProjectId, "&coverageState=suspect&level=Software")));

        // Search composes rather than replacing the coverage predicate.
        Assert.Empty(Numbers(await WorkspaceAsync(client, fixture.ProjectId, "&coverageState=covered&search=HLR-00000803")));

        // The count reflects the filtered population, and paging walks it deterministically.
        using var firstPage = await client.GetAsync(
            $"/api/enterprise-requirements/workspace?projectId={fixture.ProjectId}&page=1&pageSize=1&coverageState=suspect");
        var page = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page.GetProperty("totalPages").GetInt32());
        Assert.Equal(["HLR-00000802"], Numbers(page));
    }

    [Fact]
    /// <summary>
    /// This once asserted that an unparsable value was ignored, on the reasoning that emptying the workspace
    /// would be worse. Both were wrong: a filter that is quietly dropped shows a worklist that does not match
    /// what was asked for, and on a controlled record set that is the more dangerous of the two. An
    /// unsupported value is now refused with a stable code.
    /// </summary>
    public async Task An_unsupported_coverage_state_is_refused_rather_than_quietly_dropped()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.MemberName);

        using var response = await client.GetAsync(
            $"/api/enterprise-requirements/workspace?projectId={fixture.ProjectId}&page=1&pageSize=50&coverageState=partially");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("requirement_filter_invalid", body.GetProperty("code").GetString());
        Assert.Contains("partially", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Coverage_filtering_does_not_reach_across_a_program_boundary()
    {
        var fixture = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await SignInAsync(client, fixture.OutsiderName);

        using var response = await client.GetAsync(
            $"/api/enterprise-requirements/workspace?projectId={fixture.ProjectId}&page=1&pageSize=50&coverageState=uncovered");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
