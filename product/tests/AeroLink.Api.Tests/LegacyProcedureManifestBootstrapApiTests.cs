using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class LegacyProcedureManifestBootstrapApiTests
{
    private sealed record Fixture(Guid BaselineId, Guid ProcedureId, Guid RevisionId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Legacy Bootstrap API", "LBAPI");
        var project = new ProjectRecord(program.Id, "Legacy Product", "Legacy Software");
        var release = new SoftwareRelease(project.Id, "1.5", true);
        var request = new SystemChangeRequest("SRCR-09600", 0, project.Id, release.Id,
            "Legacy inventory", "Problem", "Analysis", "Solution", "author", now);
        request.AddRequirementChange("author", "SYSR-09600000", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The product shall preserve controlled verification evidence.",
            "Migration integrity.", "Test", now);
        request.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
        request.ApproveActiveStage("reviewer", now.AddMinutes(1));
        var baseline = new CandidateBaseline("SW-96.00", 0, project.Id, release.Id, null,
            "Released legacy baseline", "cm", now);
        baseline.Select(request, "cm", now);
        baseline.Freeze("cm", now.AddMinutes(1));
        baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 1, now.AddMinutes(2));
        baseline.MarkReleased("cm", now.AddMinutes(3));
        var procedure = new TestProcedure(project.Id, "SYSTP-096001", "Legacy smoke procedure",
            "legacy.author", now, TestProcedureLevel.System);
        var revision = new TestProcedureRevision(procedure.Id, 0, "Verify legacy smoke behavior.",
            "Configured product.", "1. Exercise the behavior.", "Behavior is correct.",
            TestProcedureState.Approved, "legacy.author", now);
        db.AddRange(program, project, release, request, baseline, procedure, revision);
        UserAccount? configurationManager = null;
        foreach (var (name, role) in new[]
                 {
                     ("legacy.cm", ProgramRole.ConfigurationManager),
                     ("legacy.reader", ProgramRole.Engineer),
                 })
        {
            var account = new UserAccount(name, name, $"{name}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
            if (role == ProgramRole.ConfigurationManager) configurationManager = account;
        }
        db.Add(new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
            configurationManager!.Id, "test.setup", now));
        await db.SaveChangesAsync();
        return new Fixture(baseline.Id, procedure.Id, revision.Id);
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task Preview_confirmation_authority_and_idempotency_are_enforced_by_the_API()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory);

        using var reader = factory.CreateClient();
        await LoginAsync(reader, "legacy.reader");
        using var deniedPreview = await reader.GetAsync(
            $"/api/baselines/{fixture.BaselineId}/legacy-procedure-manifest-bootstrap");
        Assert.Equal(HttpStatusCode.Forbidden, deniedPreview.StatusCode);
        using var deniedMutation = await reader.PostAsJsonAsync(
            $"/api/baselines/{fixture.BaselineId}/legacy-procedure-manifest-bootstrap",
            new { expectedHash = new string('0', 64), confirmLegacySnapshot = true });
        Assert.Equal(HttpStatusCode.Forbidden, deniedMutation.StatusCode);

        using var cm = factory.CreateClient();
        await LoginAsync(cm, "legacy.cm");
        using var previewResponse = await cm.GetAsync(
            $"/api/baselines/{fixture.BaselineId}/legacy-procedure-manifest-bootstrap");
        var previewBody = await previewResponse.Content.ReadAsStringAsync();
        Assert.True(previewResponse.StatusCode == HttpStatusCode.OK, previewBody);
        var preview = JsonSerializer.Deserialize<JsonElement>(previewBody);
        Assert.False(preview.GetProperty("alreadyBootstrapped").GetBoolean());
        Assert.Equal(1, preview.GetProperty("activeProcedureCount").GetInt32());
        Assert.Equal(64, preview.GetProperty("proceduresHash").GetString()!.Length);
        var hash = preview.GetProperty("proceduresHash").GetString()!;

        using var unconfirmed = await cm.PostAsJsonAsync(
            $"/api/baselines/{fixture.BaselineId}/legacy-procedure-manifest-bootstrap",
            new { expectedHash = hash, confirmLegacySnapshot = false });
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);

        using var applied = await cm.PostAsJsonAsync(
            $"/api/baselines/{fixture.BaselineId}/legacy-procedure-manifest-bootstrap",
            new { expectedHash = hash, confirmLegacySnapshot = true });
        var appliedBody = await applied.Content.ReadAsStringAsync();
        Assert.True(applied.StatusCode == HttpStatusCode.OK, appliedBody);
        Assert.True(JsonSerializer.Deserialize<JsonElement>(appliedBody)
            .GetProperty("alreadyBootstrapped").GetBoolean());

        using var retried = await cm.PostAsJsonAsync(
            $"/api/baselines/{fixture.BaselineId}/legacy-procedure-manifest-bootstrap",
            new { expectedHash = hash, confirmLegacySnapshot = true });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var member = await db.BaselineTestProcedures.SingleAsync(x => x.BaselineId == fixture.BaselineId);
        Assert.Equal(fixture.ProcedureId, member.ProcedureId);
        Assert.Equal(fixture.RevisionId, member.RevisionId);
        var recorded = await db.BaselineEvents.Where(x => x.BaselineId == fixture.BaselineId
            && x.EventType == "LegacyProcedureManifestBootstrapped").ToListAsync();
        Assert.Single(recorded);
        Assert.Equal("legacy.cm", recorded.Single().ActorId);
    }
}
