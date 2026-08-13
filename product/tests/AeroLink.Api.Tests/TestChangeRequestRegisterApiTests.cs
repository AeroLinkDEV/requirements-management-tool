using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
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

    private sealed record Seeded(Guid ProjectId, Guid ReleaseId);

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
        var legacyChange = new SystemChangeRequest("SRCR-00051", 0, project.Id, release.Id,
            "Legacy change", "P", "A", "S", "first.engineer", now);
        db.AddRange(systemChange, softwareChange, legacyChange);

        // Two revisions of one controlled package: only the newer belongs on the register.
        var first = new TestChangeReview(project.Id, release.Id, systemChange.Id, TestChangeReviewDiscipline.System,
            "SRCR-00050.00", now, baseNumber: "SYSTCR-000900", revision: 0, authorId: "first.engineer");
        first.RecordTestChangeRequired("first.engineer", now);
        first.WriteCase("first.engineer", "Superseded package", "P", "A", "S", now);
        var second = new TestChangeReview(project.Id, release.Id, systemChange.Id, TestChangeReviewDiscipline.System,
            "SRCR-00050.00", now, baseNumber: "SYSTCR-000900", revision: 1, authorId: "second.engineer");
        second.RecordTestChangeRequired("second.engineer", now);
        second.WriteCase("second.engineer", "Current package", "P", "A", "S", now);

        // A different discipline, so the discipline filter has something to exclude.
        var software = new TestChangeReview(project.Id, release.Id, softwareChange.Id,
            TestChangeReviewDiscipline.HighLevelSoftware, "HLRCR-00050.00", now,
            baseNumber: "HLRTCR-000900", revision: 0, authorId: "software.engineer");
        software.RecordTestChangeRequired("software.engineer", now);
        software.WriteCase("software.engineer", "Software package", "P", "A", "S", now);

        // A package from before controlled numbering: no BaseNumber at all.
        var legacy = new TestChangeReview(project.Id, release.Id, legacyChange.Id, TestChangeReviewDiscipline.System,
            "SRCR-00051.00", now);

        db.AddRange(first, second, software, legacy);
        await db.SaveChangesAsync();
        return new(project.Id, release.Id);
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
    public async Task Every_row_carries_what_the_register_shows()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        var body = await RegisterAsync(client, $"projectId={seeded.ProjectId}&releaseId={seeded.ReleaseId}&discipline=System&page=1&pageSize=50");
        var rows = body.GetProperty("items").EnumerateArray().ToList();
        var current = Assert.Single(rows, x => x.GetProperty("baseNumber").GetString() == "SYSTCR-000900");

        Assert.Equal("SYSTCR-000900.01", current.GetProperty("displayNumber").GetString());
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
            .Where(x => x.GetProperty("baseNumber").GetString() == "SYSTCR-000900").ToList();
        Assert.Single(numbered);
        Assert.Equal(1, numbered[0].GetProperty("revision").GetInt32());

        var behind = await RegisterAsync(client, $"projectId={seeded.ProjectId}&baseNumber=SYSTCR-000900&page=1&pageSize=50");
        Assert.Equal(2, behind.GetProperty("items").EnumerateArray().Count());
    }

    /// <summary>
    /// A package raised before controlled numbering carries an empty number. Grouping those the way numbered
    /// packages are grouped would collapse every one of them into whichever held the highest revision, so
    /// most of a Project's early packages would simply not be on its register.
    /// </summary>
    [Fact]
    public async Task A_package_with_no_controlled_number_is_still_listed()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory);

        var body = await RegisterAsync(client, $"projectId={seeded.ProjectId}&discipline=System&page=1&pageSize=50");
        var legacy = Assert.Single(body.GetProperty("items").EnumerateArray().ToList(),
            x => x.GetProperty("baseNumber").GetString() == "");

        // It reads as what it was raised from, because that is the only name it has.
        Assert.Equal("SRCR-00051.00", legacy.GetProperty("displayNumber").GetString());
        Assert.Equal(1, legacy.GetProperty("revisionCount").GetInt32());
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
        Assert.Equal("HLRTCR-000900.00", only.GetProperty("displayNumber").GetString());
    }

    [Theory]
    [InlineData("search=Current", "SYSTCR-000900.01")]
    // Searching what a package was raised from, which is how somebody arrives from the requirements side.
    [InlineData("search=SRCR-00050", "SYSTCR-000900.01")]
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
}
