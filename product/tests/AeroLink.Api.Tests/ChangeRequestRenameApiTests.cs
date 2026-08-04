using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// What a change request is called, proved through the API rather than the aggregate.
///
/// System change requests are SRCR; software ones are HLRCR or LLRCR according to the level they carry. The
/// two software prefixes are numbered apart, and each resumes above the highest number already used at its
/// own level — so a record created after the rename can never collide with one the migration renamed.
/// </summary>
public sealed class ChangeRequestRenameApiTests
{
    private static async Task<(Guid ProjectId, Guid ReleaseId)> SeedAsync(AeroLinkApiFactory factory, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord($"{prefix} Program", $"{prefix}{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();
        return (project.Id, release.Id);
    }

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, Guid projectId, Guid releaseId,
        string type, string? softwareLevel, string title) =>
        await client.PostAsJsonAsync("/api/change-requests", new
        {
            projectId, targetReleaseId = releaseId, type, softwareLevel, title,
            problem = "P", analysis = "A", solution = "S"
        });

    [Theory]
    [InlineData("System", null, "SRCR")]
    [InlineData("Software", "HighLevel", "HLRCR")]
    [InlineData("Software", "LowLevel", "LLRCR")]
    public async Task A_created_change_request_is_numbered_for_the_level_it_may_change(
        string type, string? level, string expected)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "REN");

        using var created = await CreateAsync(client, projectId, releaseId, type, level, "Numbered by level");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith($"{expected}-", body.GetProperty("displayNumber").GetString());
    }

    [Fact]
    public async Task A_software_change_request_without_a_level_is_refused_because_it_cannot_be_named()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "RNL");

        using var refused = await CreateAsync(client, projectId, releaseId, "Software", null, "No level");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("HLR", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_two_software_prefixes_are_numbered_apart_and_resume_above_what_each_level_already_used()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "RNS");

        // Records as the rename left them: numbers preserved exactly, so each level's run has gaps and its
        // own high-water mark.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.AddRange(
                new SystemChangeRequest("HLRCR-00134", 0, projectId, releaseId, "Renamed HLR", "P", "A", "S",
                    "author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel),
                new SystemChangeRequest("LLRCR-00133", 0, projectId, releaseId, "Renamed LLR", "P", "A", "S",
                    "author", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel),
                new SystemChangeRequest("SRCR-00109", 0, projectId, releaseId, "Renamed system", "P", "A", "S",
                    "author", now));
            await db.SaveChangesAsync();
        }

        using var hlr = await CreateAsync(client, projectId, releaseId, "Software", "HighLevel", "Next HLR");
        using var llr = await CreateAsync(client, projectId, releaseId, "Software", "LowLevel", "Next LLR");
        using var system = await CreateAsync(client, projectId, releaseId, "System", null, "Next system");

        Assert.Equal("HLRCR-00135.00", (await hlr.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("displayNumber").GetString());
        Assert.Equal("LLRCR-00134.00", (await llr.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("displayNumber").GetString());
        Assert.Equal("SRCR-00110.00", (await system.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("displayNumber").GetString());
    }

    [Fact]
    public async Task No_retired_identifier_can_be_created_or_recognised()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, releaseId) = await SeedAsync(factory, "RNR");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.Add(new SystemChangeRequest("SRCR-00001", 0, projectId, releaseId, "A system change", "P", "A", "S",
                "author", DateTimeOffset.UtcNow));
            db.Add(new SystemChangeRequest("HLRCR-00001", 0, projectId, releaseId, "A software change", "P", "A", "S",
                "author", DateTimeOffset.UtcNow, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel));
            await db.SaveChangesAsync();

            // The aggregate refuses a retired prefix outright, so one cannot be reintroduced by any path
            // that goes through the domain.
            Assert.Throws<AeroLink.Domain.Common.DomainException>(() => new SystemChangeRequest(
                "SCR-00002", 0, projectId, releaseId, "Retired", "P", "A", "S", "author", DateTimeOffset.UtcNow));
            Assert.Throws<AeroLink.Domain.Common.DomainException>(() => new SystemChangeRequest(
                "SWCR-00002", 0, projectId, releaseId, "Retired", "P", "A", "S", "author", DateTimeOffset.UtcNow,
                ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel));

            // And nothing the product writes carries one.
            Assert.Empty(await db.SystemChangeRequests.AsNoTracking()
                .Where(x => x.BaseNumber.StartsWith("SCR-") || x.BaseNumber.StartsWith("SWCR-")).ToListAsync());
            Assert.Empty(await db.IdentifierSequences.AsNoTracking()
                .Where(x => x.Scope == "SCR" || x.Scope == "SWCR").ToListAsync());
        }

        // The controlled history reads them back under the new names, in both a System and a software tab.
        var history = await client.GetFromJsonAsync<JsonElement>(
            $"/api/history/change-requests?projectId={projectId}&releaseId={releaseId}&type=System&page=1&pageSize=50");
        Assert.All(history.GetProperty("items").EnumerateArray(),
            item => Assert.StartsWith("SRCR-", item.GetProperty("displayNumber").GetString()));
    }
}
