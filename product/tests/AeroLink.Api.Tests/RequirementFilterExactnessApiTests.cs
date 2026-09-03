using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Tag filtering matched `TagsJson.ToLower().Contains(tag)` and owner filtering matched
/// `AttributesJson.ToLower().Contains(owner)`. Both are substring scans over serialized JSON, so the tag
/// `safe` matched every requirement tagged `failsafe`, and an owner fragment matched any attribute whose
/// value happened to contain it — including values that have nothing to do with ownership.
///
/// These drive the collisions directly, because a filter that is merely usually right is a filter nobody can
/// build a controlled worklist from.
/// </summary>
public sealed class RequirementFilterExactnessApiTests
{
    private const string Member = "filter.reader";

    private static async Task<Guid> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Filter Program", "FLT");
        var project = new ProjectRecord(program.Id, "Software", "Filter Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var scr = new SystemChangeRequest("SRCR-00500", 0, project.Id, release.Id, "Filters", "P", "A", "S", "author", now);
        var baseline = new CandidateBaseline("SW-50.00", 0, project.Id, release.Id, null, "Candidate", "cm", now);
        db.AddRange(program, project, release, scr, baseline);

        var schema = new ArtifactSchemaDefinition(project.Id, "filter-schema", "Filter schema", "System", "", "test.setup", now);
        db.Add(schema);

        // Each requirement carries one tag and one owner, chosen so that a substring match confuses them.
        (string Number, string Tag, string Owner)[] rows =
        [
            ("SYSR-00000501", "safe", "ana"),
            ("SYSR-00000502", "failsafe", "anastasia"),
            ("SYSR-00000503", "safety-critical", "diana"),
            ("SYSR-00000504", "SAFE", "ANA"),
            ("SYSR-00000505", "sûreté", "renée"),
        ];

        foreach (var (number, tag, owner) in rows)
        {
            var artifact = new RequirementArtifact(project.Id, number, RequirementLevel.System, now);
            var revision = new RequirementRevision(artifact.Id, 1, $"The FMS shall behave for {number}.", "Rationale",
                "Test", RequirementRevisionState.Active, scr.Id, baseline.Id, now);
            db.AddRange(artifact, revision);
            // An unrelated attribute deliberately holds the owner's name, which the old substring match could
            // not tell apart from ownership.
            var attributes = JsonSerializer.Serialize(new { owner, rationale = $"Reviewed with {owner} and diana." });
            db.RequirementRevisionProfiles.Add(new(revision.Id, schema.Id, "{\"blocks\":[]}", attributes,
                JsonSerializer.Serialize(new[] { tag }), "test.setup", now));
        }

        var account = new UserAccount(Member, Member, $"{Member}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task SignInAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = Member, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private static async Task<string[]> NumbersAsync(HttpClient client, Guid projectId, string query)
    {
        using var response = await client.GetAsync(
            $"/api/enterprise-requirements/workspace?projectId={projectId}&page=1&pageSize=50{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return [.. body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("baseNumber").GetString()!).Order()];
    }

    [Fact]
    public async Task A_tag_matches_itself_and_not_the_tags_it_is_a_substring_of()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);
        await SignInAsync(client);

        // The reported collision: `safe` inside `failsafe` and `safety-critical`.
        var safe = await NumbersAsync(client, projectId, "&tag=safe");
        Assert.Equal(["SYSR-00000501", "SYSR-00000504"], safe);

        Assert.Equal(["SYSR-00000502"], await NumbersAsync(client, projectId, "&tag=failsafe"));
        Assert.Equal(["SYSR-00000503"], await NumbersAsync(client, projectId, "&tag=safety-critical"));

        // A tag nobody holds returns nothing rather than everything containing the fragment.
        Assert.Empty(await NumbersAsync(client, projectId, "&tag=af"));
    }

    [Fact]
    public async Task Tag_and_owner_matching_folds_case_and_accepts_non_ascii()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);
        await SignInAsync(client);

        // Written `SAFE`, found by `safe`, and the reverse.
        Assert.Contains("SYSR-00000504", await NumbersAsync(client, projectId, "&tag=safe"));
        Assert.Contains("SYSR-00000501", await NumbersAsync(client, projectId, "&tag=SAFE"));
        Assert.Equal(["SYSR-00000504"], await NumbersAsync(client, projectId, "&owner=ana&level=System&search=00000504"));

        Assert.Equal(["SYSR-00000505"], await NumbersAsync(client, projectId, "&tag=sûreté"));
        Assert.Equal(["SYSR-00000505"], await NumbersAsync(client, projectId, "&owner=RENÉE"));
    }

    [Fact]
    public async Task An_owner_matches_the_owner_field_and_not_an_unrelated_attribute_that_mentions_them()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);
        await SignInAsync(client);

        // `diana` owns exactly one requirement, but is named in every rationale.
        Assert.Equal(["SYSR-00000503"], await NumbersAsync(client, projectId, "&owner=diana"));

        // `ana` is a prefix of `anastasia` and a substring of `diana`; it owns two, in two cases.
        Assert.Equal(["SYSR-00000501", "SYSR-00000504"], await NumbersAsync(client, projectId, "&owner=ana"));
        Assert.Equal(["SYSR-00000502"], await NumbersAsync(client, projectId, "&owner=anastasia"));
    }

    [Fact]
    public async Task An_unsupported_filter_field_or_value_is_refused_with_a_stable_code()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);
        await SignInAsync(client);

        foreach (var query in new[] { "&sort=by-vibes", "&coverageState=partially", "&level=Sideways" })
        {
            using var response = await client.GetAsync(
                $"/api/enterprise-requirements/workspace?projectId={projectId}&page=1&pageSize=5{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("requirement_filter_invalid", body.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// Two revisions of one artifact, with the baseline materialising the older one. This is the shape that
    /// tells an exact answer apart from a plausible one: "latest" would return revision 2, and the
    /// configuration under baseline actually carries revision 1.
    /// </summary>
    private static async Task<(Guid ProjectId, Guid BaselineId, Guid ArtifactId, Guid BaselinedRevisionId, Guid LatestRevisionId)> SeedTwoRevisionsAsync(
        AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Hydration Program", "HYD");
        var project = new ProjectRecord(program.Id, "Software", "Hydration Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        var scr = new SystemChangeRequest("SRCR-00600", 0, project.Id, release.Id, "Hydration", "P", "A", "S", "author", now);
        var baseline = new CandidateBaseline("SW-60.00", 0, project.Id, release.Id, null, "Candidate", "cm", now);
        db.AddRange(program, project, release, scr, baseline);

        var artifact = new RequirementArtifact(project.Id, "SYSR-00000601", RequirementLevel.System, now);
        var baselined = new RequirementRevision(artifact.Id, 1, "The FMS shall hold the baselined statement.",
            "Rationale", "Test", RequirementRevisionState.Superseded, scr.Id, baseline.Id, now);
        var latest = new RequirementRevision(artifact.Id, 2, "The FMS shall hold the newer statement.",
            "Rationale", "Test", RequirementRevisionState.Active, scr.Id, baseline.Id, now);
        db.AddRange(artifact, baselined, latest);
        db.Add(new BaselineRequirementSelection(baseline.Id, artifact.Id, baselined.Id));

        // Enough neighbours that the artifact under test cannot be answered by the first page of a listing.
        for (var index = 0; index < 5; index++)
        {
            var other = new RequirementArtifact(project.Id, $"SYSR-0000060{index + 2}", RequirementLevel.System, now);
            var revision = new RequirementRevision(other.Id, 1, $"The FMS shall behave, {index}.", "Rationale",
                "Test", RequirementRevisionState.Active, scr.Id, baseline.Id, now);
            db.AddRange(other, revision);
            db.Add(new BaselineRequirementSelection(baseline.Id, other.Id, revision.Id));
        }

        var account = new UserAccount(Member, Member, $"{Member}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return (project.Id, baseline.Id, artifact.Id, baselined.Id, latest.Id);
    }

    /// <summary>
    /// #880 §4.4 roots the Digital Thread on an exact revision, while `/traceability/{id}` has always named a
    /// requirement artifact. `ids` hydration is where the two are joined, so it has to answer to either name —
    /// and under a baseline it has to answer with the revision that configuration carries.
    /// </summary>
    [Fact]
    public async Task Hydration_answers_an_artifact_id_with_the_revision_the_baseline_carries()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seed = await SeedTwoRevisionsAsync(factory);
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/requirements?projectId={seed.ProjectId}"
            + $"&baselineId={seed.BaselineId}&page=1&pageSize=1&ids={seed.ArtifactId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items");

        var hydrated = items.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == seed.ArtifactId);
        Assert.Equal(seed.BaselinedRevisionId, hydrated.GetProperty("revisionId").GetGuid());
        Assert.NotEqual(seed.LatestRevisionId, hydrated.GetProperty("revisionId").GetGuid());
    }

    [Fact]
    public async Task Hydration_still_answers_a_revision_id_with_itself()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var seed = await SeedTwoRevisionsAsync(factory);
        await SignInAsync(client);

        // The per-kind addresses §4.4 introduces name a revision outright, and must not be re-resolved.
        using var response = await client.GetAsync($"/api/requirements?projectId={seed.ProjectId}"
            + $"&baselineId={seed.BaselineId}&page=1&pageSize=1&ids={seed.BaselinedRevisionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items");

        Assert.Contains(items.EnumerateArray(), x => x.GetProperty("revisionId").GetGuid() == seed.BaselinedRevisionId);
        // And a revision this baseline does not carry stays absent rather than being answered by its sibling.
        Assert.DoesNotContain(items.EnumerateArray(), x => x.GetProperty("revisionId").GetGuid() == seed.LatestRevisionId);
    }
}
