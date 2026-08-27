using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The register the verification side reads its change requests from.
///
/// The twin of <c>/api/history/change-requests</c>, and held to the same contract: the same query parameters,
/// the same page envelope, and the same row fields. The two registers are meant to be one register over
/// different artifacts, so what is asserted here is the shape a reader moving between them relies on.
/// </summary>
public sealed class TestChangeRequestRegisterApiTests
{
    private const string Member = "register.engineer";

    private sealed record Seeded(Guid ProjectId, Guid ReleaseId, Guid TcrId);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Register Program", "REGP");
        var project = new ProjectRecord(program.Id, "Register", "Register Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        var account = new UserAccount(Member, Member, $"{Member}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));

        // Real change requests, because a package's origin is a foreign key rather than a label.
        var systemChange = new SystemChangeRequest("SRCR-00050", 0, project.Id, release.Id,
            "System change", "P", "A", "S", "first.engineer", now);
        var softwareChange = new SystemChangeRequest("HLRCR-00050", 0, project.Id, release.Id,
            "Software change", "P", "A", "S", "software.engineer", now, ChangeRequestType.Software,
            softwareLevel: RequirementLevel.HighLevel);
        var legacySoftwareChange = new SystemChangeRequest("HLRCR-00051", 0, project.Id, release.Id,
            "Legacy software change", "P", "A", "S", "software.engineer", now, ChangeRequestType.Software,
            softwareLevel: RequirementLevel.HighLevel);
        var lowLevelChange = new SystemChangeRequest("LLRCR-00050", 0, project.Id, release.Id,
            "Low-level software change", "P", "A", "S", "software.engineer", now, ChangeRequestType.Software,
            softwareLevel: RequirementLevel.LowLevel);
        var mismatchedPrefixChange = new SystemChangeRequest("HLRCR-00052", 0, project.Id, release.Id,
            "Mismatched historical prefix", "P", "A", "S", "software.engineer", now, ChangeRequestType.Software,
            softwareLevel: RequirementLevel.HighLevel);
        var openChange = new SystemChangeRequest("SRCR-00052", 0, project.Id, release.Id,
            "Open controlled package", "P", "A", "S", "first.engineer", now);
        var legacyChange = new SystemChangeRequest("SRCR-00051", 0, project.Id, release.Id,
            "Legacy change", "P", "A", "S", "first.engineer", now);
        db.AddRange(systemChange, softwareChange, legacySoftwareChange, lowLevelChange, mismatchedPrefixChange,
            openChange, legacyChange);

        // Two revisions of one controlled package: only the newer belongs on the register.
        var first = new TestChangeReview(project.Id, release.Id, systemChange.Id, TestChangeReviewDiscipline.System,
            "SRCR-00050.00", now, baseNumber: "SYSTPCR-000900", revision: 0, authorId: "first.engineer");
        first.RecordTestChangeRequired("first.engineer", now);
        first.WriteCase("first.engineer", "Superseded package", "P", "A", "S", now);
        var second = new TestChangeReview(project.Id, release.Id, systemChange.Id, TestChangeReviewDiscipline.System,
            "SRCR-00050.00", now, baseNumber: "SYSTPCR-000900", revision: 1, authorId: "second.engineer");
        second.RecordTestChangeRequired("second.engineer", now);
        second.WriteCase("second.engineer", "Current package", "P", "A", "S", now);

        // A different discipline, so the discipline filter has something to exclude.
        var software = new TestChangeReview(project.Id, release.Id, softwareChange.Id,
            TestChangeReviewDiscipline.HighLevelSoftware, "HLRCR-00050.00", now,
            baseNumber: "HLRTCCR-000900", revision: 0, authorId: "software.engineer");
        software.RecordTestChangeRequired("software.engineer", now);
        software.WriteCase("software.engineer", "Software package", "P", "A", "S", now);

        // This automatic assessment is deliberately unnumbered until somebody concludes it needs procedure
        // work. It remains historical evidence and coverage-queue work, not a current HLRTCCR register row.
        var pendingSoftware = new TestChangeReview(project.Id, release.Id, legacySoftwareChange.Id,
            TestChangeReviewDiscipline.HighLevelSoftware, "HLRCR-00051.00", now);
        var lowLevel = new TestChangeReview(project.Id, release.Id, lowLevelChange.Id,
            TestChangeReviewDiscipline.LowLevelSoftware, "LLRCR-00050.00", now,
            baseNumber: "LLRTCCR-000901", authorId: "software.engineer");
        lowLevel.RecordTestChangeRequired("software.engineer", now);
        var mismatchedPrefix = new TestChangeReview(project.Id, release.Id, mismatchedPrefixChange.Id,
            TestChangeReviewDiscipline.HighLevelSoftware, "HLRCR-00052.00", now,
            baseNumber: "LLRTCCR-000902", authorId: "software.engineer");
        mismatchedPrefix.RecordTestChangeRequired("software.engineer", now);

        // A numbered, concluded Draft with no author is the current package the checkout test works on. The
        // unnumbered legacy row below must no longer be selected from the current register.
        var open = new TestChangeReview(project.Id, release.Id, openChange.Id, TestChangeReviewDiscipline.System,
            "SRCR-00052.00", now, baseNumber: "SYSTPCR-000901");
        open.RecordTestChangeRequired("register.engineer", now);

        // A package from before controlled numbering: no BaseNumber at all.
        var legacy = new TestChangeReview(project.Id, release.Id, legacyChange.Id, TestChangeReviewDiscipline.System,
            "SRCR-00051.00", now);

        db.AddRange(first, second, software, pendingSoftware, lowLevel, mismatchedPrefix, open, legacy);
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, second.Id);
    }

    private static async Task<HttpClient> SignInAsync(AeroLinkApiFactory factory)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = Member, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        return client;
    }

    private static async Task<JsonElement> RegisterAsync(HttpClient client, string query) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/history/test-change-requests?{query}");

    [Fact]
    public async Task Selected_TCR_trace_is_rooted_at_the_exact_TCR_and_is_project_scoped()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        using var response = await client.GetAsync($"/api/test-change-reviews/{seeded.TcrId}/trace");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var trace = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(seeded.ProjectId, trace.GetProperty("projectId").GetGuid());
        Assert.Equal(seeded.TcrId, trace.GetProperty("rootArtifactId").GetGuid());
        Assert.Equal("TestChangeRequest", trace.GetProperty("rootArtifactKind").GetString());
        Assert.Equal(Guid.Empty, trace.GetProperty("rootChangeRequestId").GetGuid());
        Assert.Contains(trace.GetProperty("nodes").EnumerateArray(), node =>
            node.GetProperty("id").GetGuid() == seeded.TcrId && node.GetProperty("kind").GetString() == "TestChangeRequest");
        Assert.Contains(trace.GetProperty("edges").EnumerateArray(), edge =>
            edge.GetProperty("toId").GetGuid() == seeded.TcrId
            && edge.GetProperty("fromKind").GetString() == "ChangeRequest");

        using var missing = await client.GetAsync($"/api/test-change-reviews/{Guid.NewGuid()}/trace");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var unauthenticated = factory.CreateClient();
        using var refused = await unauthenticated.GetAsync($"/api/test-change-reviews/{seeded.TcrId}/trace");
        Assert.True(refused.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Every_row_carries_what_the_register_shows()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        var body = await RegisterAsync(client, $"projectId={seeded.ProjectId}&releaseId={seeded.ReleaseId}&discipline=System&page=1&pageSize=50");
        var rows = body.GetProperty("items").EnumerateArray().ToList();
        var current = Assert.Single(rows, x => x.GetProperty("baseNumber").GetString() == "SYSTPCR-000900");

        Assert.Equal("SYSTPCR-000900.01", current.GetProperty("displayNumber").GetString());
        Assert.Equal("Current package", current.GetProperty("title").GetString());
        Assert.Equal("Draft", current.GetProperty("state").GetString());
        Assert.Equal("second.engineer", current.GetProperty("authorId").GetString());
        Assert.Equal(seeded.ReleaseId, current.GetProperty("targetReleaseId").GetGuid());
        Assert.Equal(0, current.GetProperty("procedureCount").GetInt32());
        // Both revisions are counted, which is what the "show superseded revisions" control offers.
        Assert.Equal(2, current.GetProperty("revisionCount").GetInt32());
        Assert.True(body.GetProperty("totalPages").GetInt32() >= 1);
    }

    /// <summary>Only the newest revision of a controlled number, exactly as the requirements register does.</summary>
    [Fact]
    public async Task Only_the_current_revision_is_listed_unless_one_number_is_asked_for()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        var listed = await RegisterAsync(client, $"projectId={seeded.ProjectId}&discipline=System&page=1&pageSize=50");
        var numbered = listed.GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("baseNumber").GetString() == "SYSTPCR-000900").ToList();
        Assert.Single(numbered);
        Assert.Equal(1, numbered[0].GetProperty("revision").GetInt32());

        var behind = await RegisterAsync(client, $"projectId={seeded.ProjectId}&baseNumber=SYSTPCR-000900&page=1&pageSize=50");
        Assert.Equal(2, behind.GetProperty("items").EnumerateArray().Count());
    }

    [Fact]
    public async Task An_unnumbered_pending_assessment_is_excluded_from_current_register_but_remains_explicit_history()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        var body = await RegisterAsync(client, $"projectId={seeded.ProjectId}&discipline=System&page=1&pageSize=50");
        Assert.DoesNotContain(body.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("baseNumber").GetString() == "");

        var historical = await RegisterAsync(client,
            $"projectId={seeded.ProjectId}&discipline=System&historical=true&page=1&pageSize=50");
        var legacy = Assert.Single(historical.GetProperty("items").EnumerateArray().ToList(),
            x => x.GetProperty("baseNumber").GetString() == "");
        Assert.Equal("SRCR-00051.00", legacy.GetProperty("displayNumber").GetString());
        Assert.Equal(1, legacy.GetProperty("revisionCount").GetInt32());
    }

    [Fact]
    public async Task The_current_register_uses_exact_HLR_and_LLR_controlled_prefixes()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        var highLevel = await RegisterAsync(client,
            $"projectId={seeded.ProjectId}&discipline=HighLevelSoftware&page=1&pageSize=50");
        var highLevelRows = highLevel.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(highLevelRows, x => x.GetProperty("baseNumber").GetString() == "HLRTCCR-000900");
        Assert.DoesNotContain(highLevelRows, x => x.GetProperty("baseNumber").GetString() == "LLRTCCR-000902");
        Assert.DoesNotContain(highLevelRows, x => x.GetProperty("baseNumber").GetString() == "");

        var lowLevel = await RegisterAsync(client,
            $"projectId={seeded.ProjectId}&discipline=LowLevelSoftware&page=1&pageSize=50");
        var lowLevelRows = lowLevel.GetProperty("items").EnumerateArray().ToList();
        var low = Assert.Single(lowLevelRows);
        Assert.Equal("LLRTCCR-000901", low.GetProperty("baseNumber").GetString());
        Assert.StartsWith("LLRTCCR-", low.GetProperty("displayNumber").GetString());
    }

    [Fact]
    public async Task The_current_register_refuses_an_absent_ladder_discipline_but_historical_read_remains_available()
    {
        await using var factory = new AeroLinkApiFactory(testLadderPolicy: SystemLowPolicy());
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        using var refused = await client.GetAsync(
            $"/api/history/test-change-requests?projectId={seeded.ProjectId}&discipline=HighLevelSoftware&page=1&pageSize=50");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("ladder_discipline_unavailable", await refused.Content.ReadAsStringAsync());

        var historical = await RegisterAsync(client,
            $"projectId={seeded.ProjectId}&discipline=HighLevelSoftware&historical=true&page=1&pageSize=50");
        Assert.Contains(historical.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("baseNumber").GetString() == "LLRTCCR-000902");
    }

    [Fact]
    public async Task The_discipline_filter_separates_the_registers()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        var system = await RegisterAsync(client, $"projectId={seeded.ProjectId}&discipline=System&page=1&pageSize=50");
        Assert.DoesNotContain(system.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("discipline").GetString() != "System");

        var software = await RegisterAsync(client, $"projectId={seeded.ProjectId}&discipline=HighLevelSoftware&page=1&pageSize=50");
        var only = Assert.Single(software.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal("HLRTCCR-000900.00", only.GetProperty("displayNumber").GetString());
    }

    [Theory]
    [InlineData("search=Current", "SYSTPCR-000900.01")]
    // Searching what a package was raised from, which is how somebody arrives from the requirements side.
    [InlineData("search=SRCR-00050", "SYSTPCR-000900.01")]
    public async Task Search_matches_the_case_and_what_the_package_was_raised_from(string query, string expected)
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        var body = await RegisterAsync(client, $"projectId={seeded.ProjectId}&discipline=System&{query}&page=1&pageSize=50");
        Assert.Contains(body.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("displayNumber").GetString() == expected);
    }

    [Fact]
    public async Task An_unknown_lifecycle_state_is_refused_rather_than_ignored()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        using var response = await client.GetAsync(
            $"/api/history/test-change-requests?projectId={seeded.ProjectId}&state=Nonsense&page=1&pageSize=50");

        // Silently returning everything would show a filtered register that is not filtered.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_register_is_refused_to_somebody_outside_the_project()
    {
        await using var factory = new AeroLinkApiFactory();
        await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        using var response = await client.GetAsync(
            $"/api/history/test-change-requests?projectId={Guid.NewGuid()}&page=1&pageSize=50");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A package is the record that governs procedure change, so it is the record with a working copy.</summary>
    [Fact]
    public async Task A_draft_package_can_be_checked_out()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);
        var listed = await RegisterAsync(client, $"projectId={seeded.ProjectId}&discipline=System&page=1&pageSize=50");
        var id = listed.GetProperty("items").EnumerateArray()
            // The package nobody raised: it has no author, so it is open to any test engineer in the Project — the
            // same people who could always author its decisions.
            .First(x => x.GetProperty("authorId").GetString() == "").GetProperty("id").GetGuid();

        using var opened = await client.PostAsJsonAsync("/api/controlled-editing/checkout",
            new { artifactType = "TestChangeRequest", artifactId = id });

        var body = await opened.Content.ReadAsStringAsync();
        Assert.True(opened.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{(int)opened.StatusCode}: {body}");
        // Checked out as a test change request, through the universal mechanism rather than a private one.
        Assert.Contains("\"artifactType\":\"TestChangeRequest\"", body);
        // The working copy carries the package as it stands, so the engineer edits what is there.
        Assert.Contains("procedureChanges", body);

        var session = JsonDocument.Parse(body).RootElement;
        var draft = JsonNode.Parse(session.GetProperty("draftJson").GetString()!)!.AsObject();
        draft["title"] = "Checked-in verification package";
        draft["problem"] = "The verification case needs a controlled correction.";
        using var saved = await client.PutAsJsonAsync(
            $"/api/controlled-editing/sessions/{session.GetProperty("id").GetGuid()}/autosave",
            new { expectedVersion = session.GetProperty("version").GetInt64(), draftJson = draft.ToJsonString() });
        var savedBody = await saved.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedVersion = JsonDocument.Parse(savedBody).RootElement.GetProperty("version").GetInt64();

        using var checkedIn = await client.PostAsJsonAsync(
            $"/api/controlled-editing/sessions/{session.GetProperty("id").GetGuid()}/check-in",
            new { expectedVersion = savedVersion });
        var checkedInBody = await checkedIn.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal("Checked-in verification package", (await verificationDb.TestChangeReviews.FindAsync(id))!.Title);
    }

    private static ILadderPolicy SystemLowPolicy()
    {
        var configuration = ProjectLadderConfiguration.CreateDraft(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var system = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.System, 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities, now);
        var low = new ProjectLadderStep(configuration.Id, configuration.ProjectId, RequirementLevel.LowLevel, 2,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities, now);
        configuration.Steps.Add(system);
        configuration.Steps.Add(low);
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(configuration.Id, configuration.ProjectId,
            system.Id, low.Id, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }
}
