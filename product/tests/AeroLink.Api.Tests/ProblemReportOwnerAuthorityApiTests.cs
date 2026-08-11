using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProblemReportOwnerAuthorityApiTests
{
    [Fact]
    public async Task Reassignment_requires_current_program_membership_and_accountable_owner_authority()
    {
        using var factory = new AeroLinkApiFactory();
        var scenario = await SeedAsync(factory);
        using var owner = factory.CreateClient();
        await LoginAsync(owner, "owner.engineer");
        var report = await CreateAsync(owner, scenario.ProjectId);

        var directory = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/directory?projectId={scenario.ProjectId}&authority={ProblemReportOwnerAuthority.DirectoryAuthority}&search=authority&limit=50");
        var offered = directory.EnumerateArray().Select(item => item.GetProperty("userName").GetString()).ToList();
        Assert.Contains("authority.system", offered);
        Assert.Contains("authority.software", offered);
        Assert.DoesNotContain("authority.quality", offered);
        Assert.DoesNotContain("authority.outsider", offered);
        Assert.DoesNotContain("authority.other-program", offered);

        await RefusedAsync(owner, report.Id, report.Version, "authority.outsider",
            "pr_owner_program_membership_required");
        await RefusedAsync(owner, report.Id, report.Version, "authority.other-program",
            "pr_owner_program_membership_required");
        await RefusedAsync(owner, report.Id, report.Version, "authority.disabled",
            "pr_owner_account_unavailable");
        await RefusedAsync(owner, report.Id, report.Version, "authority.quality",
            "pr_owner_authority_required");

        using var accepted = await owner.PostAsJsonAsync($"/api/problem-reports/{report.Id}/owner", new
        {
            expectedVersion = report.Version,
            responsibleEngineerId = "authority.system"
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        var acceptedVersion = acceptedBody.GetProperty("version").GetInt64();

        using var stale = await owner.PostAsJsonAsync($"/api/problem-reports/{report.Id}/owner", new
        {
            expectedVersion = report.Version,
            responsibleEngineerId = "authority.software"
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("stale_version", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{report.Id}");
        Assert.Equal("authority.system", detail.GetProperty("responsibleEngineerId").GetString());
        Assert.Equal(acceptedVersion, detail.GetProperty("version").GetInt64());
        Assert.Contains(detail.GetProperty("revisions").EnumerateArray(), item =>
            item.GetProperty("eventType").GetString() == "ResponsibleEngineerReassigned");

        using var newOwner = factory.CreateClient();
        await LoginAsync(newOwner, "authority.system");
        using var work = await newOwner.PostAsJsonAsync($"/api/problem-reports/{report.Id}/ready-for-sccb",
            new { expectedVersion = acceptedVersion });
        Assert.Equal(HttpStatusCode.OK, work.StatusCode);
    }

    [Fact]
    public async Task Membership_loss_is_visible_and_explicit_supervision_can_recover_without_rewriting_history()
    {
        using var factory = new AeroLinkApiFactory();
        var scenario = await SeedAsync(factory);
        using var owner = factory.CreateClient();
        await LoginAsync(owner, "owner.engineer");
        var report = await CreateAsync(owner, scenario.ProjectId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var accountId = await db.UserAccounts.Where(item => item.UserName == "owner.engineer")
                .Select(item => item.Id).SingleAsync();
            var memberships = await db.ProgramMemberships
                .Where(item => item.UserId == accountId && item.ProgramId == scenario.ProgramId).ToListAsync();
            db.ProgramMemberships.RemoveRange(memberships);
            await db.SaveChangesAsync();
        }

        using var inaccessible = await owner.GetAsync($"/api/problem-reports/{report.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, inaccessible.StatusCode);

        using var supervisor = factory.CreateClient();
        await LoginAsync(supervisor, "owner.supervisor");
        var exception = await supervisor.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{report.Id}");
        var capabilities = exception.GetProperty("capabilities");
        Assert.Equal("owner.engineer", exception.GetProperty("responsibleEngineerId").GetString());
        Assert.False(capabilities.GetProperty("ownerEligible").GetBoolean());
        Assert.True(capabilities.GetProperty("canRecoverOwner").GetBoolean());
        Assert.Contains("no longer", capabilities.GetProperty("ownerAuthorityException").GetString(), StringComparison.OrdinalIgnoreCase);

        using (var quality = factory.CreateClient())
        {
            await LoginAsync(quality, "authority.quality");
            using var refusedRecovery = await quality.PostAsJsonAsync($"/api/problem-reports/{report.Id}/owner", new
            {
                expectedVersion = report.Version,
                responsibleEngineerId = "authority.software"
            });
            Assert.Equal(HttpStatusCode.Forbidden, refusedRecovery.StatusCode);
        }

        using var recovered = await supervisor.PostAsJsonAsync($"/api/problem-reports/{report.Id}/owner", new
        {
            expectedVersion = report.Version,
            responsibleEngineerId = "authority.software"
        });
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var recoveredVersion = (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        var detail = await supervisor.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{report.Id}");
        Assert.Equal("authority.software", detail.GetProperty("responsibleEngineerId").GetString());
        Assert.True(detail.GetProperty("capabilities").GetProperty("ownerEligible").GetBoolean());
        Assert.False(detail.GetProperty("capabilities").GetProperty("canRecoverOwner").GetBoolean());
        Assert.Contains(detail.GetProperty("revisions").EnumerateArray(), item =>
            item.GetProperty("eventType").GetString() == "ProblemReportCreated"
            && item.GetProperty("snapshotJson").GetString()!.Contains("owner.engineer", StringComparison.Ordinal));
        Assert.Contains(detail.GetProperty("revisions").EnumerateArray(), item =>
            item.GetProperty("eventType").GetString() == "ResponsibleEngineerReassigned"
            && item.GetProperty("actor").GetString() == "owner.supervisor");

        using var overrideValidOwner = await supervisor.PostAsJsonAsync($"/api/problem-reports/{report.Id}/owner", new
        {
            expectedVersion = recoveredVersion,
            responsibleEngineerId = "authority.system"
        });
        Assert.Equal(HttpStatusCode.Forbidden, overrideValidOwner.StatusCode);
        var unchanged = await supervisor.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{report.Id}");
        Assert.Equal("authority.software", unchanged.GetProperty("responsibleEngineerId").GetString());
    }

    private static async Task RefusedAsync(HttpClient client, Guid reportId, long version, string target, string code)
    {
        using var response = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/owner", new
        {
            expectedVersion = version,
            responsibleEngineerId = target
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.DoesNotContain("@example.test", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(target, body.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(Guid Id, long Version)> CreateAsync(HttpClient client, Guid projectId)
    {
        using var response = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId,
            title = "Accountable owner authority",
            problem = "A controlled record must never be assigned outside its Program authority."
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("version").GetInt64());
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName,
            password = AeroLinkApiFactory.MemberPassword
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<Scenario> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Problem Report Owner Program", $"PO{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Accountable Product", "Flight Software");
        var otherProgram = new ProgramRecord("Other Owner Program", $"PX{Guid.NewGuid():N}"[..12]);
        db.AddRange(program, project, otherProgram);

        Add("owner.engineer", ProgramRole.Engineer, program.Id);
        Add("owner.supervisor", ProgramRole.ProgramManager, program.Id);
        Add("authority.system", ProgramRole.SystemEngineer, program.Id);
        Add("authority.software", ProgramRole.SoftwareEngineer, program.Id);
        Add("authority.quality", ProgramRole.SoftwareQualityAnalyst, program.Id);
        Add("authority.other-program", ProgramRole.Engineer, otherProgram.Id);
        Add("authority.outsider", null, null);
        var disabled = Add("authority.disabled", ProgramRole.Engineer, program.Id);
        disabled.Disable(now);
        await db.SaveChangesAsync();
        return new(program.Id, project.Id);

        UserAccount Add(string userName, ProgramRole? role, Guid? programId)
        {
            var account = new UserAccount(userName, userName, $"{userName}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            if (role is not null && programId is not null)
                db.Add(new ProgramMembership(account.Id, programId.Value, role.Value, "test.setup", now));
            return account;
        }
    }

    private sealed record Scenario(Guid ProgramId, Guid ProjectId);
}
