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
/// A downstream conclusion can be wrong, and saying so has to be an act of its own rather than pressing the
/// same button a second time. These cover who may withdraw a conclusion, what survives the withdrawal, and
/// that the surface reports which of those is available rather than leaving the client to guess.
/// </summary>
public sealed class DownstreamAssessmentReopenApiTests
{
    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static UserAccount Account(string user, DateTimeOffset now) => new(user, user,
        $"{user}@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);

    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid AssessmentId, Guid DraftId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Reopen Authority", "RPA");
        var project = new ProjectRecord(program.Id, "FMS", "Reopen FMS");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var source = new SystemChangeRequest("SCR-91001", 0, project.Id, release.Id, "Upstream change",
            "Problem", "Analysis", "Solution", "author", now);
        var draft = new SystemChangeRequest("SWCR-91002", 0, project.Id, release.Id, "Downstream work",
            "Problem", "Analysis", "Solution", "software.engineer", now, ChangeRequestType.Software,
            softwareLevel: RequirementLevel.HighLevel);
        var assessment = new DownstreamChangeAssessment(project.Id, release.Id, source.Id,
            source.DisplayNumber, RequirementLevel.HighLevel, now);
        var engineer = Account("software.engineer", now);
        var other = Account("other.engineer", now);
        var approver = Account("assurance.reviewer", now);
        db.AddRange(program, project, release, source, draft, assessment, engineer, other, approver,
            new ProgramMembership(engineer.Id, program.Id, ProgramRole.Engineer, "setup", now),
            new ProgramMembership(other.Id, program.Id, ProgramRole.Engineer, "setup", now),
            new ProgramMembership(approver.Id, program.Id, ProgramRole.Approver, "setup", now));
        await db.SaveChangesAsync();
        return new Fixture(project.Id, release.Id, assessment.Id, draft.Id);
    }

    private static async Task<JsonElement> RowAsync(HttpClient client, Fixture fixture)
    {
        var rows = await client.GetFromJsonAsync<JsonElement>(
            $"/api/downstream-assessments?projectId={fixture.ProjectId}&releaseId={fixture.ReleaseId}");
        return rows[0];
    }

    [Fact]
    public async Task An_assessment_with_no_conclusion_offers_nothing_to_withdraw()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory);
        using var engineer = factory.CreateClient();
        await LoginAsync(engineer, "software.engineer");
        await SecurityBoundaryTests.AuthorizeMutationsAsync(engineer);

        // Unclaimed.
        Assert.False((await RowAsync(engineer, fixture)).GetProperty("capabilities").GetProperty("canReopen").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await engineer.PostAsJsonAsync(
            $"/api/downstream-assessments/{fixture.AssessmentId}/assign", new { engineerId = "software.engineer" })).StatusCode);
        // Claimed but undecided: still nothing to withdraw.
        Assert.False((await RowAsync(engineer, fixture)).GetProperty("capabilities").GetProperty("canReopen").GetBoolean());
        using var refused = await engineer.PostAsJsonAsync(
            $"/api/downstream-assessments/{fixture.AssessmentId}/reopen", new { reason = "Nothing was decided." });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task The_engineer_who_concluded_can_withdraw_it_and_the_previous_answer_is_kept()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory);
        using var engineer = factory.CreateClient();
        await LoginAsync(engineer, "software.engineer");
        await SecurityBoundaryTests.AuthorizeMutationsAsync(engineer);
        await engineer.PostAsJsonAsync($"/api/downstream-assessments/{fixture.AssessmentId}/assign",
            new { engineerId = "software.engineer" });
        await engineer.PostAsync($"/api/downstream-assessments/{fixture.AssessmentId}/change-required", null);
        Assert.Equal(HttpStatusCode.OK, (await engineer.PostAsJsonAsync(
            $"/api/downstream-assessments/{fixture.AssessmentId}/change-requests",
            new { changeRequestId = fixture.DraftId })).StatusCode);

        var concluded = await RowAsync(engineer, fixture);
        Assert.Equal("ChangeRequestsLinked", concluded.GetProperty("outcome").GetString());
        // The conclusion names its author, so the drawer can state whose answer it is.
        Assert.Equal("software.engineer", concluded.GetProperty("decidedBy").GetString());
        Assert.True(concluded.GetProperty("capabilities").GetProperty("canReopen").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await engineer.PostAsJsonAsync(
            $"/api/downstream-assessments/{fixture.AssessmentId}/reopen",
            new { reason = "SWCR-91002 answers a different System change." })).StatusCode);

        var reopened = await RowAsync(engineer, fixture);
        Assert.Equal("Pending", reopened.GetProperty("outcome").GetString());
        Assert.Equal("Open", reopened.GetProperty("state").GetString());
        Assert.Empty(reopened.GetProperty("linkedChangeRequests").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("decidedBy").ValueKind);
        // The assignment survives: it is the same engineer's work, now unanswered.
        Assert.Equal("software.engineer", reopened.GetProperty("assignedEngineerId").GetString());

        var withdrawn = Assert.Single(reopened.GetProperty("reopenings").EnumerateArray());
        Assert.Equal("ChangeRequestsLinked", withdrawn.GetProperty("previousOutcome").GetString());
        Assert.Equal("software.engineer", withdrawn.GetProperty("previousDecidedBy").GetString());
        Assert.Equal("SWCR-91002.00", withdrawn.GetProperty("detachedChangeRequestNumbers").GetString());
        Assert.Contains("different System change", withdrawn.GetProperty("reason").GetString());

        // The detached Draft SWCR itself is untouched — only the assessment's claim on it was withdrawn.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.NotNull(await db.SystemChangeRequests.SingleOrDefaultAsync(x => x.Id == fixture.DraftId));
    }

    [Fact]
    public async Task Another_engineer_cannot_withdraw_a_conclusion_that_is_not_theirs()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory);
        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "software.engineer");
            await SecurityBoundaryTests.AuthorizeMutationsAsync(engineer);
            await engineer.PostAsJsonAsync($"/api/downstream-assessments/{fixture.AssessmentId}/assign",
                new { engineerId = "software.engineer" });
            await engineer.PostAsJsonAsync($"/api/downstream-assessments/{fixture.AssessmentId}/no-change",
                new { rationale = "The existing HLR behavior already satisfies the change." });
        }
        using var intruder = factory.CreateClient();
        await LoginAsync(intruder, "other.engineer");
        await SecurityBoundaryTests.AuthorizeMutationsAsync(intruder);
        Assert.False((await RowAsync(intruder, fixture)).GetProperty("capabilities").GetProperty("canReopen").GetBoolean());
        using var refused = await intruder.PostAsJsonAsync(
            $"/api/downstream-assessments/{fixture.AssessmentId}/reopen", new { reason = "I read it differently." });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Withdrawing_an_approved_conclusion_takes_approval_authority()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory);
        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "software.engineer");
            await SecurityBoundaryTests.AuthorizeMutationsAsync(engineer);
            await engineer.PostAsJsonAsync($"/api/downstream-assessments/{fixture.AssessmentId}/assign",
                new { engineerId = "software.engineer" });
            await engineer.PostAsJsonAsync($"/api/downstream-assessments/{fixture.AssessmentId}/no-change",
                new { rationale = "The existing HLR behavior already satisfies the change." });
            Assert.Equal(HttpStatusCode.OK, (await engineer.PostAsJsonAsync(
                $"/api/downstream-assessments/{fixture.AssessmentId}/submit",
                new { approverId = "assurance.reviewer" })).StatusCode);
            // In review, the assessment is with its approver: it is returned, never withdrawn behind them.
            var inReview = await RowAsync(engineer, fixture);
            Assert.False(inReview.GetProperty("capabilities").GetProperty("canReopen").GetBoolean());
        }
        using var approver = factory.CreateClient();
        await LoginAsync(approver, "assurance.reviewer");
        await SecurityBoundaryTests.AuthorizeMutationsAsync(approver);
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsync(
            $"/api/downstream-assessments/{fixture.AssessmentId}/approve", null)).StatusCode);

        var approved = await RowAsync(approver, fixture);
        Assert.True(approved.GetProperty("capabilities").GetProperty("canReopen").GetBoolean());
        // The engineer who wrote the conclusion no longer owns it once it is approved.
        using (var engineer = factory.CreateClient())
        {
            await LoginAsync(engineer, "software.engineer");
            await SecurityBoundaryTests.AuthorizeMutationsAsync(engineer);
            Assert.False((await RowAsync(engineer, fixture)).GetProperty("capabilities").GetProperty("canReopen").GetBoolean());
            using var refused = await engineer.PostAsJsonAsync(
                $"/api/downstream-assessments/{fixture.AssessmentId}/reopen", new { reason = "Second thoughts." });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync(
            $"/api/downstream-assessments/{fixture.AssessmentId}/reopen",
            new { reason = "A later reading of the System change shows an HLR gap." })).StatusCode);

        var reopened = await RowAsync(approver, fixture);
        Assert.Equal("Open", reopened.GetProperty("state").GetString());
        Assert.Equal("Pending", reopened.GetProperty("outcome").GetString());
        var withdrawn = Assert.Single(reopened.GetProperty("reopenings").EnumerateArray());
        Assert.Equal("Approved", withdrawn.GetProperty("previousState").GetString());
        Assert.Equal("assurance.reviewer", withdrawn.GetProperty("previousApprovedBy").GetString());
        Assert.Equal("assurance.reviewer", withdrawn.GetProperty("actorId").GetString());
    }

    [Fact]
    public async Task A_released_build_refuses_the_withdrawal_like_every_other_change()
    {
        using var factory = new AeroLinkApiFactory();
        var fixture = await SeedAsync(factory);
        using var engineer = factory.CreateClient();
        await LoginAsync(engineer, "software.engineer");
        await SecurityBoundaryTests.AuthorizeMutationsAsync(engineer);
        await engineer.PostAsJsonAsync($"/api/downstream-assessments/{fixture.AssessmentId}/assign",
            new { engineerId = "software.engineer" });
        await engineer.PostAsJsonAsync($"/api/downstream-assessments/{fixture.AssessmentId}/no-change",
            new { rationale = "The existing HLR behavior already satisfies the change." });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            await db.Releases.Where(x => x.Id == fixture.ReleaseId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsReleased, true));
        }

        using var refused = await engineer.PostAsJsonAsync(
            $"/api/downstream-assessments/{fixture.AssessmentId}/reopen", new { reason = "Reassessing." });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.False((await RowAsync(engineer, fixture)).GetProperty("capabilities").GetProperty("canReopen").GetBoolean());
    }
}
