using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class VerificationImpactApiTests
{
    private sealed record Fixture(Guid ReleaseId, Guid BaselineId, Guid ProjectId);

    /// <summary>
    /// Builds a Program with one approved change request that introduces a requirement, plus a candidate
    /// baseline that selects it. The verification impact raised by that approval is what the tests exercise.
    /// </summary>
    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory, params (string user, ProgramRole role)[] members)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Verification Program", "VIP");
        var project = new ProjectRecord(program.Id, "Software", "Verification Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var scr = new SystemChangeRequest("SCR-00000900", 0, project.Id, release.Id, "Oceanic", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", "SYSR-00000901", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The FMS shall sequence oceanic waypoints.", "New capability", "Analysis", now);
        scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        var baseline = new CandidateBaseline("SWBL-00000900", 0, project.Id, release.Id, null, "Candidate", "cm", now);
        baseline.Select(scr, "cm", now);
        db.AddRange(program, project, release, scr, baseline);

        foreach (var (user, role) in members)
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        // Raise the work exactly as change-request approval does.
        var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
        var tracked = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == scr.Id);
        await impact.RaiseForApprovedChangeRequestAsync(tracked, now, default);
        await db.SaveChangesAsync();

        return new Fixture(release.Id, baseline.Id, project.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task An_unresolved_verification_impact_blocks_the_baseline_from_being_frozen()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory,
            ("cm.user", ProgramRole.ConfigurationManager), ("lead.user", ProgramRole.TestLead), ("eng.user", ProgramRole.TestEngineer));

        await LoginAsync(client, "cm.user");
        using var blocked = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/freeze", new { });
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        var error = (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
        Assert.Contains("verification impact", error);
        Assert.Contains("SYSR-00000901", error);

        // The verification engineer confirms no test is required — the declared method alone never sufficed.
        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "eng.user");
            var items = await engineer.GetFromJsonAsync<JsonElement>($"/api/releases/{fixture.ReleaseId}/verification-impact?outstandingOnly=true");
            Assert.Equal(1, items.GetArrayLength());
            var item = items[0];
            Assert.Equal("Analysis", item.GetProperty("declaredVerificationMethod").GetString());
            Assert.True(item.GetProperty("blocksBaselineApproval").GetBoolean());

            using var resolved = await engineer.PostAsJsonAsync($"/api/verification-impact/{item.GetProperty("id").GetGuid()}/resolve",
                new { outcome = "NoTestRequired", rationale = "Verified by analysis report AR-114." });
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        }

        using var allowed = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/freeze", new { });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Confirming_coverage_requires_an_approved_procedure_not_prose()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, ("eng.user", ProgramRole.TestEngineer));
        await LoginAsync(client, "eng.user");

        var items = await client.GetFromJsonAsync<JsonElement>($"/api/releases/{fixture.ReleaseId}/verification-impact");
        var id = items[0].GetProperty("id").GetGuid();

        using var noProcedure = await client.PostAsJsonAsync($"/api/verification-impact/{id}/resolve",
            new { outcome = "ProcedureCoverageConfirmed", rationale = "Trust me, it is covered." });
        Assert.Equal(HttpStatusCode.BadRequest, noProcedure.StatusCode);

        using var unknownProcedure = await client.PostAsJsonAsync($"/api/verification-impact/{id}/resolve",
            new { outcome = "ProcedureCoverageConfirmed", rationale = "Covered.", procedureId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, unknownProcedure.StatusCode);
    }

    [Fact]
    public async Task Only_the_test_lead_distributes_work_and_only_verification_engineers_resolve_it()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory,
            ("author.user", ProgramRole.Engineer), ("lead.user", ProgramRole.TestLead), ("eng.user", ProgramRole.TestEngineer));

        Guid id;
        using (var lead = factory.CreateClient())
        {
            await LoginAsync(lead, "lead.user");
            var items = await lead.GetFromJsonAsync<JsonElement>($"/api/releases/{fixture.ReleaseId}/verification-impact");
            id = items[0].GetProperty("id").GetGuid();

            using var assigned = await lead.PostAsJsonAsync($"/api/verification-impact/{id}/assign", new { engineerId = "eng.user" });
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
            var body = await assigned.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Assigned", body.GetProperty("state").GetString());
            Assert.Equal("eng.user", body.GetProperty("assignedEngineerId").GetString());
            Assert.Equal("lead.user", body.GetProperty("assignedByLeadId").GetString());
            Assert.True(body.GetProperty("blocksBaselineApproval").GetBoolean());
        }

        // The requirement author cannot distribute the work, nor decide what it means for verification.
        await LoginAsync(client, "author.user");
        using var cannotAssign = await client.PostAsJsonAsync($"/api/verification-impact/{id}/assign", new { engineerId = "eng.user" });
        Assert.Equal(HttpStatusCode.Forbidden, cannotAssign.StatusCode);
        using var cannotResolve = await client.PostAsJsonAsync($"/api/verification-impact/{id}/resolve",
            new { outcome = "NoTestRequired", rationale = "I wrote it, it is fine." });
        Assert.Equal(HttpStatusCode.Forbidden, cannotResolve.StatusCode);
    }
}
