using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// A new requirement is given a place in the document by the person writing it.
///
/// The author could previously leave the section alone, and the form said so: "Decide when the baseline is
/// assembled". That sounds like deferral and is really abdication — the requirement lands wherever a backfill
/// puts it, and the one person who knew where it belonged has by then finished and moved on.
///
/// A modification is different. It already sits somewhere, so leaving it alone is a real answer, and the
/// existing option stays. A retirement has no section to be in at all.
/// </summary>
public sealed class AuthoredSectionTests
{
    private static async Task<(Guid ProjectId, Guid ReleaseId, Guid SectionId)> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Authored Section Program", "ASP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Authored Section Software");
        var release = new SoftwareRelease(project.Id, "2.0", false);
        db.AddRange(program, project, release);

        var specification = new RequirementSpecification(project.Id, "SYSRD-000001", "System Requirements Document",
            RequirementLevel.System.ToString(), "Authoritative structured system requirements document.", "seed", now);
        db.Add(specification);
        var section = new SpecificationNode(specification.Id, null, 1000, SpecificationNodeType.Section,
            "Functional Behavior", null, "seed", now);
        db.Add(section);

        var account = new UserAccount("section.author", "section.author", "section.author@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return (project.Id, release.Id, section.Id);
    }

    private static object Body(Guid projectId, Guid releaseId, string kind, Guid? sectionId, string baseNumber = "")
        => new
        {
            projectId,
            targetReleaseId = releaseId,
            title = "Oceanic waypoint sequencing",
            problem = "P",
            analysis = "A",
            solution = "S",
            type = "System",
            requirementChanges = new[]
            {
                new
                {
                    baseNumber,
                    revision = 0,
                    level = "System",
                    kind,
                    statement = "The FMS shall sequence oceanic waypoints.",
                    rationale = "New",
                    verificationMethod = "Test",
                    targetSectionId = sectionId,
                },
            },
        };

    /// <summary>An unfinished draft is still saved. Refusing to store somebody's work in progress helps nobody.</summary>
    [Fact]
    public async Task A_draft_may_be_saved_before_its_section_has_been_chosen()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, releaseId, _) = await SeedAsync(factory);
        await SignInAsync(client);

        using var response = await client.PostAsJsonAsync("/api/change-request-drafts",
            Body(projectId, releaseId, "Introduce", null));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
    }

    [Fact]
    public async Task A_new_requirement_without_a_section_cannot_be_sent_for_review()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, releaseId, _) = await SeedAsync(factory);
        await SignInAsync(client);

        var draft = await CreateDraftAsync(client, Body(projectId, releaseId, "Introduce", null));
        using var response = await client.PostAsJsonAsync($"/api/change-requests/{draft.Id}/submit", new
        {
            expectedVersion = draft.Version,
            mode = "Sequential",
            approvers = new[] { new { userId = "section.author", name = "Section Author" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Says which document and what has to be done, rather than "bad request" against a form where nothing
        // the author typed was wrong.
        Assert.Contains("System requirements document section", body);
        Assert.Contains("this new requirement belongs in", body);
    }

    [Fact]
    public async Task A_new_requirement_with_a_section_reaches_review()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, releaseId, sectionId) = await SeedAsync(factory);
        await SignInAsync(client);

        var draft = await CreateDraftAsync(client, Body(projectId, releaseId, "Introduce", sectionId));
        using var response = await client.PostAsJsonAsync($"/api/change-requests/{draft.Id}/submit", new
        {
            expectedVersion = draft.Version,
            mode = "Sequential",
            approvers = new[] { new { userId = "section.author", name = "Section Author" } },
        });
        var body = await response.Content.ReadAsStringAsync();
        // Asserted against the body, so a refusal explains itself rather than arriving as a bare status.
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
    }

    private static async Task<DraftResponse> CreateDraftAsync(HttpClient client, object body)
    {
        using var created = await client.PostAsJsonAsync("/api/change-request-drafts", body);
        Assert.True(created.StatusCode == HttpStatusCode.Created,
            $"{(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        return (await created.Content.ReadFromJsonAsync<DraftResponse>())!;
    }

    private sealed record DraftResponse(Guid Id, long Version);

    // The other half of the rule — that a modification may still be left where it already is — needs a
    // requirement that exists in a materialized baseline, which is a showcase's worth of setup. It is proven
    // against real data by the "modifying a requirement offers to leave it where it already is" journey.

    private static async Task SignInAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "section.author", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
