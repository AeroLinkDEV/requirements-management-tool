using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class CaseProcedureLifecycleApiTests
{
    [Fact]
    public async Task Shared_controls_enforce_test_authority_and_append_exact_attributed_outcomes()
    {
        var policy = ProcedureEnabledTestPolicy.Create();
        using var factory = new AeroLinkApiFactory(testLadderPolicy: policy);
        var fixture = await SeedAsync(factory, policy);

        using (var viewer = factory.CreateClient())
        {
            await LoginAsync(viewer, "case.viewer");
            using var denied = await viewer.PostAsJsonAsync(
                $"/api/case-procedure-links/{fixture.LinkId}/lifecycle/acknowledge",
                new { rationale = "A project member cannot make a verification disposition." });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using var engineer = factory.CreateClient();
        await LoginAsync(engineer, "case.lifecycle");
        using (var acknowledge = await engineer.PostAsJsonAsync(
                   $"/api/case-procedure-links/{fixture.LinkId}/lifecycle/acknowledge",
                   new { rationale = "The exact Procedure relationship is under controlled assessment." }))
        {
            Assert.True(acknowledge.IsSuccessStatusCode, await acknowledge.Content.ReadAsStringAsync());
        }
        using (var changeRequired = await engineer.PostAsJsonAsync(
                   $"/api/case-procedure-links/{fixture.LinkId}/lifecycle/resolve",
                   new
                   {
                       outcome = "DownstreamChangeRequiredNotYetApproved",
                       rationale = "The existing Procedure requires approved work before this relation closes.",
                   }))
        {
            Assert.True(changeRequired.IsSuccessStatusCode, await changeRequired.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await changeRequired.Content.ReadAsStringAsync());
            Assert.Equal("ChangeRequired", body.RootElement.GetProperty("state").GetString());
        }
        using (var close = await engineer.PostAsJsonAsync(
                   $"/api/case-procedure-links/{fixture.LinkId}/lifecycle/resolve",
                   new
                   {
                       outcome = "ExistingDownstreamRevisionRemainsValid",
                       rationale = "Approved reassessment confirms the existing exact Procedure revision remains valid.",
                   }))
            Assert.True(close.IsSuccessStatusCode, await close.Content.ReadAsStringAsync());

        using var read = await engineer.GetAsync($"/api/case-procedure-links/{fixture.LinkId}/lifecycle");
        Assert.True(read.IsSuccessStatusCode, await read.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("CaseProcedure", root.GetProperty("linkKind").GetString());
        Assert.Equal("Closed", root.GetProperty("state").GetString());
        Assert.Equal("ExistingDownstreamRevisionRemainsValid", root.GetProperty("outcome").GetString());
        Assert.Equal(fixture.CaseRevisionId,
            root.GetProperty("causeVerificationRevisionId").GetGuid());
        var events = root.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["Raised", "Acknowledged", "ResolutionRecorded", "ResolutionRecorded"],
            events.Select(x => x.GetProperty("type").GetString()));
        Assert.Equal(["baseline.materializer", "case.lifecycle", "case.lifecycle", "case.lifecycle"],
            events.Select(x => x.GetProperty("actorId").GetString()));
        Assert.All(events, item => Assert.Equal(fixture.CaseRevisionId,
            item.GetProperty("causeVerificationRevisionId").GetGuid()));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(4, await db.ExactLinkSuspectEvents.AsNoTracking()
            .CountAsync(x => x.LinkId == fixture.LinkId));
    }

    private sealed record Fixture(Guid LinkId, Guid CaseRevisionId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory, ILadderPolicy policy)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Case Procedure API lifecycle", "CPAL");
        var project = new ProjectRecord(program.Id, "Software", "Case Procedure API lifecycle project");
        var engineer = new UserAccount("case.lifecycle", "Case Lifecycle Engineer",
            "case.lifecycle@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var viewer = new UserAccount("case.viewer", "Case Lifecycle Viewer",
            "case.viewer@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var caseArtifact = new TestProcedure(project.Id, "HLRTC-727200", "Controlled Case",
            "case.author", now, TestProcedureLevel.HighLevel, policy, VerificationArtifactKind.Case);
        var caseRevision0 = new TestProcedureRevision(caseArtifact.Id, 0, "Original Case", "Setup", "Steps",
            "Expected", TestProcedureState.Approved, "case.author", now,
            parentKind: VerificationProcedureParentKind.Derived, derivedRationale: "Focused API fixture.");
        var procedure = new TestProcedure(project.Id, "HLRTP-727200", "Controlled Procedure",
            "procedure.author", now, TestProcedureLevel.HighLevel, policy,
            VerificationArtifactKind.Procedure, VerificationProcedureParentKind.Allocated);
        var procedureRevision = new TestProcedureRevision(procedure.Id, 0, "Procedure", "Bench", "Execute",
            "Observe", TestProcedureState.Draft, "procedure.author", now, environmentSetup: "Bench",
            testData: "Controlled data", orderedSteps: "Execute", expectedObservations: "Observe",
            cleanup: "Restore", toolingAutomation: "Qualified runner",
            parentKind: VerificationProcedureParentKind.Allocated);
        var historicalLink = new TestCaseProcedureLink(caseRevision0.Id, procedureRevision.Id);
        db.AddRange(program, project, engineer, viewer,
            new ProgramMembership(engineer.Id, program.Id, ProgramRole.TestEngineer, "issue-727", now),
            new ProgramMembership(viewer.Id, program.Id, ProgramRole.Engineer, "issue-727", now),
            caseArtifact, caseRevision0, procedure, procedureRevision, historicalLink);
        await db.SaveChangesAsync();
        db.Entry(procedureRevision).Property(x => x.State).CurrentValue = TestProcedureState.Approved;
        await db.SaveChangesAsync();

        var caseRevision1 = new TestProcedureRevision(caseArtifact.Id, 1, "Changed Case", "Setup", "Steps",
            "Expected", TestProcedureState.Approved, "case.author", now,
            parentKind: VerificationProcedureParentKind.Derived, derivedRationale: "Focused API fixture.");
        var link = new TestCaseProcedureLink(caseRevision1.Id, procedureRevision.Id);
        var lifecycle = ExactLinkSuspectLifecycle.Raise(project.Id, ExactLinkKind.CaseProcedure, link.Id,
            ExactLinkLifecycleCauseKind.InternalVerificationRevision, null, null, "baseline.materializer",
            "The exact Case revision changed and its Procedure relation requires assessment.",
            now.AddSeconds(-1), caseRevision1.Id);
        link.AttachExactLinkLifecycle(lifecycle.Id);
        db.AddRange(caseRevision1, link, lifecycle);
        db.ExactLinkSuspectEvents.AddRange(lifecycle.Events);
        await db.SaveChangesAsync();
        return new(link.Id, caseRevision1.Id);
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName,
            password = AeroLinkApiFactory.MemberPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
