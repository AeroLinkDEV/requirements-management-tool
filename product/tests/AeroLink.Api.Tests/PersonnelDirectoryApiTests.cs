using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The #816 Slice 3 directory and identity contract: adding a person grants several base roles as one
/// logical attributable operation; the directory searches active accounts and never offers current
/// members; the current display name and email are editable by the global administrator only, with the
/// change audited and historical attribution untouched.
/// </summary>
public sealed class PersonnelDirectoryApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public PersonnelDirectoryApiTests(SharedApiHost host) => _host = host;

    private sealed record Seeded(Guid ProjectId, Guid ProgramId, Guid MemberId, Guid CandidateId, string MemberName, string CandidateName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var memberName = $"directory.member.{tag}";
        var candidateName = $"directory.candidate.{tag}";
        var program = new ProgramRecord($"Directory Program {tag}", $"DIR{tag}");
        var project = new ProjectRecord(program.Id, "Flight Software", "Directory Software");
        db.AddRange(program, project);
        var member = new UserAccount(memberName, memberName, $"{memberName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var candidate = new UserAccount(candidateName, candidateName, $"{candidateName}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(member, candidate);
        db.Add(new ProgramMembership(member.Id, program.Id, ProgramRole.SystemEngineer, "test.setup", now));
        await db.SaveChangesAsync();
        return new(project.Id, program.Id, member.Id, candidate.Id, memberName, candidateName);
    }

    private static async Task<HttpClient> AdminAsync(AeroLinkApiFactory factory)
    {
        var client = factory.CreateClient();
        // The three tests of this class share one database: the first run bootstraps the global
        // administrator, later runs only need the login. Either way the login below must succeed.
        string bootstrapOutcome;
        using (var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new { displayName = "AeroLink Administrator", email = "admin@example.test", password = AeroLinkApiFactory.AdministratorPassword })
        })
        {
            bootstrap.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
            using var response = await client.SendAsync(bootstrap);
            bootstrapOutcome = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
        }
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.True(login.IsSuccessStatusCode, $"admin login failed: {login.StatusCode} {await login.Content.ReadAsStringAsync()} (bootstrap: {bootstrapOutcome})");
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
        return client;
    }

    [Fact]
    public async Task Adding_a_person_grants_several_base_roles_as_one_operation()
    {
        using var client = await AdminAsync(_host.Factory);
        var seeded = await SeedAsync(_host.Factory);

        var added = await client.PostAsJsonAsync($"/api/projects/{seeded.ProjectId}/personnel",
            new { userId = seeded.CandidateId, roles = new[] { "SystemEngineer", "Airworthiness" } });
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var grants = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.UserId == seeded.CandidateId && x.EndedAt == null).ToListAsync();
        Assert.Equal(2, grants.Count);
        Assert.All(grants, x => Assert.Equal("admin", x.GrantedBy));
    }

    [Fact]
    public async Task The_directory_searches_name_username_and_email_and_skips_current_members()
    {
        using var client = await AdminAsync(_host.Factory);
        var seeded = await SeedAsync(_host.Factory);

        var byEmail = await client.GetAsync($"/api/projects/{seeded.ProjectId}/personnel/candidates?search={seeded.CandidateName}@example.test");
        Assert.Equal(HttpStatusCode.OK, byEmail.StatusCode);
        var found = await byEmail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, found.GetArrayLength());
        Assert.Equal(seeded.CandidateId, found[0].GetProperty("userId").GetGuid());

        // The current member is never offered as a new person; the roster's Edit roles path covers them.
        var byMember = await client.GetAsync($"/api/projects/{seeded.ProjectId}/personnel/candidates?search={seeded.MemberName}");
        var memberResults = await byMember.Content.ReadAsStringAsync();
        Assert.DoesNotContain(seeded.MemberId.ToString(), memberResults);
    }

    [Fact]
    public async Task Only_the_global_administrator_edits_current_identity_and_the_change_is_audited()
    {
        using var admin = await AdminAsync(_host.Factory);
        var seeded = await SeedAsync(_host.Factory);

        var updated = await admin.PatchAsJsonAsync($"/api/admin/users/{seeded.CandidateId}/identity",
            new { displayName = "Renamed Candidate", email = "renamed.candidate@example.test" });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);

        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var account = await db.UserAccounts.AsNoTracking().SingleAsync(x => x.Id == seeded.CandidateId);
        Assert.Equal("Renamed Candidate", account.DisplayName);
        Assert.Equal("renamed.candidate@example.test", account.Email);
        Assert.True(await db.SecurityAuditEvents.AnyAsync(x => x.EventType == "IdentityUpdated"
            && x.Target == seeded.CandidateName && x.Detail.Contains("Renamed Candidate")));

        // A non-administrator is refused outright, and a malformed email never reaches the account.
        using var memberClient = _host.Factory.CreateClient();
        using (var login = await memberClient.PostAsJsonAsync("/api/auth/login",
            new { userName = seeded.MemberName, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var forbidden = await memberClient.PatchAsJsonAsync($"/api/admin/users/{seeded.CandidateId}/identity",
            new { displayName = "Hostile Rename", email = "hostile@example.test" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var invalid = await admin.PatchAsJsonAsync($"/api/admin/users/{seeded.CandidateId}/identity",
            new { displayName = "Renamed Candidate", email = "not-an-email" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("Renamed Candidate", (await db.UserAccounts.AsNoTracking().SingleAsync(x => x.Id == seeded.CandidateId)).DisplayName);
    }
}
