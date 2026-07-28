using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// What the traceability graph says a proposed change touches, offered to the author deciding its impact.
///
/// A change request asks its author to close five impact decisions, two of which — trace relationships and
/// verification coverage — are answerable from links the product already holds. Those links were reachable from
/// the requirements explorer and from nowhere near the person actually deciding, so the decision was made from
/// memory beside a database that knew the answer.
///
/// The line these tests hold is that informing is not deciding. The endpoint reports; it must never write a
/// disposition, and a requirement with nothing downstream must still leave its author something to confirm.
/// </summary>
public sealed class AuthoringTracedImpactTests
{
    private sealed record Traced(string BaseNumber, bool Known, string? DisplayNumber, Guid? RequirementRevisionId,
        TracedRequirement[] DerivedRequirements, TracedProcedure[] CoveringProcedures);
    private sealed record TracedRequirement(Guid Id, string DisplayNumber, string Level, string Statement, string LinkType);
    private sealed record TracedProcedure(Guid Id, Guid RevisionId, string DisplayNumber, string Title, string Level,
        string State, bool IsSuspect, string CoverageState);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A parent requirement, a child that derives from it, and a procedure that verifies the parent.</summary>
    private static async Task<(Guid ProjectId, string ParentNumber, string ChildNumber, string ProcedureNumber)> SeedAsync(
        AeroLinkApiFactory factory, bool suspectCoverage = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Traced Program", "TRP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Traced Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        var account = new UserAccount("traced.author", "traced.author", "traced.author@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));

        // A revision records the change request and baseline it came from, so those exist rather than being
        // faked with empty identifiers the foreign keys would reject.
        var origin = new SystemChangeRequest("SCR-00000500", 0, project.Id, release.Id, "Origin", "P", "A", "S", "traced.author", now);
        var baseline = new CandidateBaseline("SWBL-00000500", 0, project.Id, release.Id, null, "Origin baseline", "cm", now);
        db.AddRange(origin, baseline);

        var parent = new RequirementArtifact(project.Id, "SYSR-000501", RequirementLevel.System, now);
        var child = new RequirementArtifact(project.Id, "HLR-000502", RequirementLevel.HighLevel, now);
        db.AddRange(parent, child);
        var parentRevision = new RequirementRevision(parent.Id, 0, "The FMS shall sequence oceanic waypoints.",
            "Rationale", "Test", RequirementRevisionState.Active, origin.Id, baseline.Id, now);
        var childRevision = new RequirementRevision(child.Id, 0, "The software shall compute the sequence.",
            "Rationale", "Test", RequirementRevisionState.Active, origin.Id, baseline.Id, now);
        db.AddRange(parentRevision, childRevision);

        // The child traces up to the parent: source derives from target. A change to the parent therefore
        // propagates down to the child, which is the direction the endpoint has to read.
        db.RequirementTraces.Add(new RequirementTraceLink(project.Id, childRevision.Id, parentRevision.Id,
            RequirementTraceType.DerivedFrom, "Derived from the system requirement.", now));

        var procedure = new TestProcedure(project.Id, "SYSTP-000503", "Verify oceanic sequencing",
            "test.author", now, TestProcedureLevel.System);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Purpose", "Configuration", "Steps",
            "Expected", TestProcedureState.Approved, "test.author", now);
        db.AddRange(procedure, procedureRevision);
        db.TestCoverage.Add(suspectCoverage
            ? TestRequirementCoverage.CarriedForward(procedureRevision.Id, parentRevision.Id,
                "The parent requirement wording changed.", now)
            : new TestRequirementCoverage(procedureRevision.Id, parentRevision.Id));

        await db.SaveChangesAsync();
        return (project.Id, parent.BaseNumber, child.BaseNumber, procedure.BaseNumber);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "traced.author", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task A_requirement_reports_what_derives_from_it_and_what_verifies_it()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, childNumber, procedureNumber) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/authoring/impact?projectId={projectId}&baseNumber={parentNumber}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        var traced = JsonSerializer.Deserialize<Traced>(body, Json)!;

        Assert.True(traced.Known);
        Assert.Equal($"{parentNumber}.00", traced.DisplayNumber);
        Assert.Equal(childNumber, Assert.Single(traced.DerivedRequirements).DisplayNumber[..childNumber.Length]);
        Assert.Equal("HighLevel", traced.DerivedRequirements[0].Level);
        Assert.Equal(procedureNumber, Assert.Single(traced.CoveringProcedures).DisplayNumber[..procedureNumber.Length]);
        Assert.Equal("Approved", traced.CoveringProcedures[0].State);
        Assert.Equal("Confirmed", traced.CoveringProcedures[0].CoverageState);
        Assert.False(traced.CoveringProcedures[0].IsSuspect);
        Assert.NotEqual(Guid.Empty, traced.CoveringProcedures[0].RevisionId);
        Assert.NotNull(traced.RequirementRevisionId);
    }

    [Fact]
    public async Task A_suspect_link_is_reported_separately_from_the_approved_procedure_lifecycle()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, _, _) = await SeedAsync(factory, suspectCoverage: true);
        await SignInAsync(client);

        var traced = await client.GetFromJsonAsync<Traced>(
            $"/api/authoring/impact?projectId={projectId}&baseNumber={parentNumber}", Json);

        var procedure = Assert.Single(traced!.CoveringProcedures);
        Assert.Equal("Approved", procedure.State);
        Assert.True(procedure.IsSuspect);
        Assert.Equal("Suspect", procedure.CoverageState);
    }

    /// <summary>
    /// The direction matters. A change to the parent propagates to what derives from it; opening the child must
    /// not report its parent as something this change affects, or an author closes a trace disposition against
    /// the wrong set.
    /// </summary>
    [Fact]
    public async Task Only_what_derives_from_the_requirement_is_reported_not_what_it_derives_from()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _, childNumber, _) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/authoring/impact?projectId={projectId}&baseNumber={childNumber}");
        var traced = JsonSerializer.Deserialize<Traced>(await response.Content.ReadAsStringAsync(), Json)!;

        Assert.True(traced.Known);
        Assert.Empty(traced.DerivedRequirements);
        Assert.Empty(traced.CoveringProcedures);
    }

    /// <summary>
    /// A requirement being introduced does not exist yet. That is the ordinary case for the commonest kind of
    /// proposal, so it answers rather than 404s — an authoring surface that errors on a new requirement is worse
    /// than one that says there is nothing recorded.
    /// </summary>
    [Fact]
    public async Task A_requirement_that_does_not_exist_yet_answers_rather_than_failing()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _, _, _) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/authoring/impact?projectId={projectId}&baseNumber=SYSR-999999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var traced = JsonSerializer.Deserialize<Traced>(await response.Content.ReadAsStringAsync(), Json)!;
        Assert.False(traced.Known);
        Assert.Empty(traced.DerivedRequirements);
        Assert.Empty(traced.CoveringProcedures);
    }

    /// <summary>
    /// The one that matters: reading the traces must not decide anything.
    ///
    /// The five dispositions are the author's, and a proposal is not review-ready until a person has closed each
    /// one. If reading this endpoint quietly marked trace or verification as decided, a change request could
    /// reach review carrying a machine's opinion dressed as an engineer's.
    /// </summary>
    [Fact]
    public async Task Reading_the_traces_changes_no_disposition()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, parentNumber, _, _) = await SeedAsync(factory);
        await SignInAsync(client);

        var releaseId = await ReleaseIdAsync(factory, projectId);
        using var created = await client.PostAsJsonAsync("/api/scr-drafts", new
        {
            projectId,
            targetReleaseId = releaseId,
            title = "Modify oceanic sequencing",
            problem = "P", analysis = "A", solution = "S",
            type = "System",
            requirementChanges = new[]
            {
                new { baseNumber = parentNumber, revision = 1, level = "System", kind = "Modify",
                      statement = "The FMS shall sequence oceanic waypoints deterministically.",
                      rationale = "Clarified", verificationMethod = "Test" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var scrId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetGuid();

        using (var read = await client.GetAsync($"/api/authoring/impact?projectId={projectId}&baseNumber={parentNumber}"))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var detail = await client.GetAsync($"/api/scrs/{scrId}");
        var change = JsonSerializer.Deserialize<JsonElement>(await detail.Content.ReadAsStringAsync())
            .GetProperty("requirementChanges").EnumerateArray().Single();
        var dispositions = JsonSerializer.Deserialize<Dictionary<string, string>>(
            change.GetProperty("impactDispositionJson").GetString() ?? "{}") ?? [];

        // Every area still Pending, or absent entirely. Either way, nothing was decided on the author's behalf.
        foreach (var area in new[] { "trace", "verification", "documents", "baseline", "collaboration" })
            Assert.True(!dispositions.TryGetValue(area, out var value) || value == "Pending",
                $"{area} was set to '{dispositions.GetValueOrDefault(area)}' by reading the traces.");
    }

    private static async Task<Guid> ReleaseIdAsync(AeroLinkApiFactory factory, Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        return await Task.FromResult(db.Releases.Single(x => x.ProjectId == projectId).Id);
    }
}
