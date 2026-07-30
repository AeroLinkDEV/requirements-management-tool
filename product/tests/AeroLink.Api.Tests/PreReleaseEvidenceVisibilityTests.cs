using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Work that still holds the release stays visible as work.
///
/// `blocksBaselineApproval` answers one question — is this decision still outstanding — and everything
/// downstream took it for the whole answer. An item resolved with "evidence required before release"
/// therefore reported that it blocked nothing, while the release readiness gate went on refusing to ship
/// until that evidence arrived. The verification workspace filters its queue on exactly that flag, so the
/// one place an engineer looks for outstanding work had quietly dropped it.
/// </summary>
public sealed class PreReleaseEvidenceVisibilityTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid ImpactId, Guid ProcedureId, Guid ProcedureRevisionId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Evidence Program", "EVP");
        var project = new ProjectRecord(program.Id, "Software", "Evidence Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var scr = new SystemChangeRequest("SCR-00910", 0, project.Id, release.Id, "Oceanic", "P", "A", "S", "author", now);
        scr.AddRequirementChange("author", "SYSR-00000911", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The FMS shall sequence oceanic waypoints.", "New capability", "Analysis", now);
        scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
        scr.ApproveActiveStage("reviewer", now);
        db.AddRange(program, project, release, scr);

        var procedure = new TestProcedure(project.Id, "SYSTP-000900", "Oceanic sequencing", "evidence.engineer", now,
            TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Objective", "Preconditions",
            "Steps", "Expected", TestProcedureState.Draft, "evidence.engineer", now);
        revision.Approve("evidence.lead");
        db.AddRange(procedure, revision);

        foreach (var (user, role) in new[]
                 {
                     ("evidence.engineer", ProgramRole.TestEngineer),
                     ("evidence.lead", ProgramRole.TestLead),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        var tracked = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == scr.Id);
        await impact.RaiseForApprovedChangeRequestAsync(tracked, now, default);
        await db.SaveChangesAsync();

        var item = await db.VerificationImpactItems.AsNoTracking().FirstAsync(x => x.ReleaseId == release.Id);
        return new(project.Id, release.Id, item.Id, procedure.Id, revision.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<JsonElement> ResolveRequiringEvidenceAsync(HttpClient client, Fixture fixture)
    {
        using var assigned = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.ImpactId}/assign",
            new { engineerId = "evidence.engineer" });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());

        using var resolved = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.ImpactId}/resolve", new
        {
            outcome = "ProcedureCoverageConfirmed",
            rationale = "Covered by the approved oceanic sequencing procedure.",
            procedureId = fixture.ProcedureId,
            preReleaseEvidenceRequired = true,
        });
        var body = await resolved.Content.ReadAsStringAsync();
        Assert.True(resolved.IsSuccessStatusCode, $"{(int)resolved.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    [Fact]
    public async Task An_item_owing_pre_release_evidence_still_reports_that_it_holds_the_release()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "evidence.lead");
        await ResolveRequiringEvidenceAsync(client, fixture);

        using var listed = await client.GetAsync($"/api/releases/{fixture.ReleaseId}/verification-impact");
        var items = JsonSerializer.Deserialize<JsonElement>(await listed.Content.ReadAsStringAsync());
        var item = items.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == fixture.ImpactId);

        // The decision itself is made, so this stays false — it is answering a different question.
        Assert.False(item.GetProperty("blocksBaselineApproval").GetBoolean());
        // What a reader actually needs: the build cannot ship until this item's evidence arrives.
        Assert.True(item.GetProperty("awaitsPreReleaseEvidence").GetBoolean());
        Assert.True(item.GetProperty("holdsRelease").GetBoolean());
    }

    /// <summary>An item that never designated pre-release evidence is finished when it is resolved.</summary>
    [Fact]
    public async Task An_item_resolved_without_designating_evidence_holds_nothing()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "evidence.lead");

        using var assigned = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.ImpactId}/assign",
            new { engineerId = "evidence.engineer" });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        using var resolved = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.ImpactId}/resolve", new
        {
            outcome = "NoTestRequired",
            rationale = "The change is editorial and needs no test.",
            procedureId = (Guid?)null,
            preReleaseEvidenceRequired = false,
        });
        Assert.True(resolved.IsSuccessStatusCode, await resolved.Content.ReadAsStringAsync());

        using var listed = await client.GetAsync($"/api/releases/{fixture.ReleaseId}/verification-impact");
        var items = JsonSerializer.Deserialize<JsonElement>(await listed.Content.ReadAsStringAsync());
        var item = items.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == fixture.ImpactId);

        Assert.False(item.GetProperty("awaitsPreReleaseEvidence").GetBoolean());
        Assert.False(item.GetProperty("holdsRelease").GetBoolean());
    }
}
