using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// What the new job titles actually mean when the product is asked.
///
/// The enum is easy; the consequence is not. Somebody recorded as a System Engineer has to be able to do an
/// engineer's work, and somebody recorded as Airworthiness has to be able to see everything without being
/// able to change engineering content. Both are asserted here against real endpoints rather than against the
/// role table, because the role table is not what refuses a request.
/// </summary>
public sealed class ProgramRoleAuthorityApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid SectionId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Authority Program", "AUT");
        var project = new ProjectRecord(program.Id, "Flight Software", "Authority Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var specification = new RequirementSpecification(project.Id, "SYSRD-000001", "System Requirements Document",
            RequirementLevel.System.ToString(), "Seeded.", "test.setup", now);
        var section = new SpecificationNode(specification.Id, null, 1000, SpecificationNodeType.Section,
            "Functional Behavior", null, "test.setup", now);
        db.AddRange(program, project, release, specification, section);

        foreach (var (user, role) in new[]
                 {
                     ("precise.engineer", ProgramRole.SystemEngineer),
                     ("airworthiness.reader", ProgramRole.Airworthiness),
                     ("quality.reader", ProgramRole.SoftwareQualityAnalyst),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, section.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static object Draft(Fixture fixture) => new
    {
        projectId = fixture.ProjectId,
        targetReleaseId = fixture.ReleaseId,
        type = "System",
        title = "Oceanic waypoint sequencing",
        problem = "P",
        analysis = "A",
        solution = "S",
        requirementChanges = new[]
        {
            new
            {
                level = "System", kind = "Introduce",
                statement = "The FMS shall sequence oceanic waypoints.",
                rationale = "New", verificationMethod = "Test",
                targetSectionId = fixture.SectionId,
            },
        },
    };

    /// <summary>
    /// The lockout this guards against: replacing somebody's generic Engineer membership with the precise
    /// title they hold would otherwise take away the authoring they do every day.
    /// </summary>
    [Fact]
    public async Task A_system_engineer_can_author_without_also_holding_the_generic_engineer_role()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "precise.engineer");

        using var response = await client.PostAsJsonAsync("/api/change-request-drafts", Draft(fixture));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
    }

    [Theory]
    [InlineData("airworthiness.reader")]
    [InlineData("quality.reader")]
    public async Task An_oversight_role_reads_the_Program_without_being_able_to_author_in_it(string user)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, user);

        // Reads everything: membership alone is what grants that, and these roles are members.
        using var read = await client.GetAsync($"/api/change-requests?projectId={fixture.ProjectId}");
        Assert.True(read.IsSuccessStatusCode, $"{(int)read.StatusCode}: {await read.Content.ReadAsStringAsync()}");

        // And holds no authority over engineering content, which is the whole point of an oversight role.
        using var write = await client.PostAsJsonAsync("/api/change-request-drafts", Draft(fixture));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
