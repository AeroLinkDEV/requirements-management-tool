using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class SoftwareChangeRequestLevelHistoryTests
{
    [Fact]
    public async Task Empty_software_draft_requires_and_persists_its_workspace_level()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId, releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Draft scope program", $"DS{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Flight Software", "Scoped Software");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var account = new UserAccount("scope.engineer", "Scope Engineer", "scope@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, account, new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
            await db.SaveChangesAsync(); projectId = project.Id; releaseId = release.Id;
        }
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/login", new { userName = "scope.engineer", password = AeroLinkApiFactory.MemberPassword });
        var body = new { projectId, targetReleaseId = releaseId, type = "Software", title = "Empty scoped Draft", problem = "P", analysis = "A", solution = "S" };
        using var rejected = await client.PostAsJsonAsync("/api/change-request-drafts", new { body.projectId, body.targetReleaseId, body.type, body.title, body.problem, body.analysis, body.solution, requirementChanges = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var created = await client.PostAsJsonAsync("/api/change-request-drafts", new { body.projectId, body.targetReleaseId, body.type, body.title, body.problem, body.analysis, body.solution, softwareLevel = "LowLevel", requirementChanges = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LowLevel", draft.GetProperty("softwareLevel").GetString());
        var low = await client.GetFromJsonAsync<JsonElement>($"/api/history/change-requests?projectId={projectId}&releaseId={releaseId}&type=Software&level=LowLevel&page=1&pageSize=50");
        Assert.Contains(low.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == draft.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Software_history_filters_by_the_level_each_change_request_declares()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId;
        Guid releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Software scope program", "SSP");
            var project = new ProjectRecord(program.Id, "Flight Software", "Scoped Software");
            var release = new SoftwareRelease(project.Id, "1.6", false);
            var account = new UserAccount("scope.engineer", "Scope Engineer", "scope@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, account,
                new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));

            var hlr = SoftwareRequest("HLRCR-00001", "HLR only", project.Id, release.Id, now,
                ("HLR-000001", RequirementLevel.HighLevel));
            var llr = SoftwareRequest("LLRCR-00002", "LLR only", project.Id, release.Id, now,
                ("LLR-000001", RequirementLevel.LowLevel));
            // There is deliberately no mixed-level fixture. A software change request declares HLR or LLR
            // scope before it exists — its identifier depends on it — and the authoring endpoint already
            // refused to create one holding both. The only way to build a mixed request was to construct it
            // through the domain with no level at all, which is a state the product never let a person reach.
            var emptyHlr = new SystemChangeRequest("HLRCR-00004", 0, project.Id, release.Id, "Empty HLR Draft", "P", "A", "S",
                "scope.engineer", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
            var emptyLlr = new SystemChangeRequest("LLRCR-00005", 0, project.Id, release.Id, "Empty LLR Draft", "P", "A", "S",
                "scope.engineer", now, ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel);
            var system = new SystemChangeRequest("SRCR-00001", 0, project.Id, release.Id,
                "System only", "P", "A", "S", "scope.engineer", now);
            system.AddRequirementChange("scope.engineer", "SYSR-000001", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "System statement", "R", "Test", now);
            db.AddRange(hlr, llr, emptyHlr, emptyLlr, system);
            await db.SaveChangesAsync();
            projectId = project.Id;
            releaseId = release.Id;
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "scope.engineer", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var high = await ReadAsync(client,
            $"/api/history/change-requests?projectId={projectId}&releaseId={releaseId}&type=Software&level=HighLevel&page=1&pageSize=50");
        // The HLR tab holds every HLRCR, whether it carries changes yet or not — including the empty Draft,
        // which is exactly the record a level-inferred filter would have lost.
        Assert.Equal(["HLRCR-00001.00", "HLRCR-00004.00"], Numbers(high));
        var authored = high.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("displayNumber").GetString() == "HLRCR-00001.00");
        Assert.True(authored.GetProperty("hasHighLevelChanges").GetBoolean());
        Assert.False(authored.GetProperty("hasLowLevelChanges").GetBoolean());

        using var low = await ReadAsync(client,
            $"/api/history/change-requests?projectId={projectId}&releaseId={releaseId}&type=Software&level=LowLevel&page=1&pageSize=50");
        Assert.Equal(["LLRCR-00002.00", "LLRCR-00005.00"], Numbers(low));

        using var systems = await ReadAsync(client,
            $"/api/history/change-requests?projectId={projectId}&releaseId={releaseId}&type=System&page=1&pageSize=50");
        Assert.Equal(["SRCR-00001.00"], Numbers(systems));
    }

    private static SystemChangeRequest SoftwareRequest(string number, string title, Guid projectId, Guid releaseId,
        DateTimeOffset now, params (string Number, RequirementLevel Level)[] changes)
    {
        var request = new SystemChangeRequest(number, 0, projectId, releaseId, title, "P", "A", "S",
            "scope.engineer", now, ChangeRequestType.Software, softwareLevel: number.StartsWith("LLRCR-") ? RequirementLevel.LowLevel : RequirementLevel.HighLevel);
        foreach (var change in changes)
            request.AddRequirementChange("scope.engineer", change.Number, 0, change.Level,
                RequirementChangeKind.Introduce, $"{change.Level} statement", "R", "Test", now);
        return request;
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string[] Numbers(JsonDocument page) => page.RootElement.GetProperty("items")
        .EnumerateArray().Select(item => item.GetProperty("displayNumber").GetString()!).OrderBy(x => x).ToArray();
}
