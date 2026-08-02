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
    public async Task Software_history_filters_by_requirement_level_and_keeps_mixed_requests_in_both_tabs()
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

            var hlr = SoftwareRequest("SWCR-00001", "HLR only", project.Id, release.Id, now,
                ("HLR-000001", RequirementLevel.HighLevel));
            var llr = SoftwareRequest("SWCR-00002", "LLR only", project.Id, release.Id, now,
                ("LLR-000001", RequirementLevel.LowLevel));
            var mixed = SoftwareRequest("SWCR-00003", "Mixed scope", project.Id, release.Id, now,
                ("HLR-000002", RequirementLevel.HighLevel), ("LLR-000002", RequirementLevel.LowLevel));
            var system = new SystemChangeRequest("SCR-00001", 0, project.Id, release.Id,
                "System only", "P", "A", "S", "scope.engineer", now);
            system.AddRequirementChange("scope.engineer", "SYSR-000001", 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "System statement", "R", "Test", now);
            db.AddRange(hlr, llr, mixed, system);
            await db.SaveChangesAsync();
            projectId = project.Id;
            releaseId = release.Id;
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "scope.engineer", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var high = await ReadAsync(client,
            $"/api/history/scrs?projectId={projectId}&releaseId={releaseId}&type=Software&level=HighLevel&page=1&pageSize=50");
        Assert.Equal(["SWCR-00001.00", "SWCR-00003.00"], Numbers(high));
        var mixedHigh = high.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("displayNumber").GetString() == "SWCR-00003.00");
        Assert.True(mixedHigh.GetProperty("hasHighLevelChanges").GetBoolean());
        Assert.True(mixedHigh.GetProperty("hasLowLevelChanges").GetBoolean());

        using var low = await ReadAsync(client,
            $"/api/history/scrs?projectId={projectId}&releaseId={releaseId}&type=Software&level=LowLevel&page=1&pageSize=50");
        Assert.Equal(["SWCR-00002.00", "SWCR-00003.00"], Numbers(low));

        using var systems = await ReadAsync(client,
            $"/api/history/scrs?projectId={projectId}&releaseId={releaseId}&type=System&page=1&pageSize=50");
        Assert.Equal(["SCR-00001.00"], Numbers(systems));
    }

    private static SystemChangeRequest SoftwareRequest(string number, string title, Guid projectId, Guid releaseId,
        DateTimeOffset now, params (string Number, RequirementLevel Level)[] changes)
    {
        var request = new SystemChangeRequest(number, 0, projectId, releaseId, title, "P", "A", "S",
            "scope.engineer", now, ChangeRequestType.Software);
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
