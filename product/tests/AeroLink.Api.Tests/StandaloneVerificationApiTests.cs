using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// DEC-144: a project that uses Verification without Requirements.
///
/// A Standalone verification artifact names no parents because there is no requirement to trace it to. It is
/// reviewed, approved and baselined like any other, and the rule that it exists only without Requirements is
/// the save boundary's, so every entry point meets it.
/// </summary>
public sealed class StandaloneVerificationApiTests
{
    private const ProjectFeature WithoutRequirements =
        ProjectFeature.Verification | ProjectFeature.ProblemReports | ProjectFeature.Release;

    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid BaselineId, Guid ReportId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory, ProjectFeature features)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Standalone Verification", "SAV");
        var project = new ProjectRecord(program.Id, "Bench", "Standalone Bench");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        db.AddRange(program, project, release, new ProjectFeatureSet(project.Id, features, "test.setup", now));
        await db.SaveChangesAsync();

        var report = new ProblemReport(project.Id, "PR-00001", "Bench rig drops frames",
            "Frames are lost under load.", "Analysis", "reporter", now, targetReleaseId: release.Id);
        var baseline = new CandidateBaseline("SW-00.01", 0, project.Id, release.Id, null, "Standalone baseline", "cm", now);
        db.AddRange(report, baseline);
        UserAccount? configurationManager = null;
        foreach (var (user, role) in new[] { ("standalone.cm", ProgramRole.ConfigurationManager) })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
            configurationManager = account;
        }
        db.Add(new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
            configurationManager!.Id, "test.setup", now));
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, baseline.Id, report.Id);
    }

    /// <summary>An approved System package raised from the Problem Report, proposing one procedure.</summary>
    private static TestChangeReview Package(Fixture fixture, string number, string procedure, int revision,
        TestProcedureChangeKind kind, VerificationProcedureParentKind parentKind)
    {
        var now = DateTimeOffset.UtcNow;
        var review = TestChangeReview.FromProblemReport(fixture.ProjectId, fixture.ReleaseId, fixture.ReportId,
            TestChangeReviewDiscipline.System, "PR-00001.00", now, number, authorId: "verification.engineer");
        review.RecordTestChangeRequired("verification.engineer", now);
        review.AddProcedureChange("verification.engineer", new TestProcedureChangeDraft(procedure, revision,
            TestProcedureLevel.System, kind, "Frame capture under load", "Show the rig keeps every frame at load.",
            "Rig powered.", "1. Apply load. 2. Count frames.", "No frame is lost.", "The rig loses frames.",
            ParentKind: parentKind), now);
        review.WriteCase("verification.engineer", "Bench frame loss", "Problem", "Analysis", "Solution", now);
        return review;
    }

    private static async Task SaveAsync(AeroLinkApiFactory factory, TestChangeReview review, bool approve = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        if (approve)
        {
            var now = DateTimeOffset.UtcNow;
            review.Submit("verification.engineer", "test.lead", true, now);
            review.Approve("test.lead", "Reviewed.", now);
        }
        db.Add(review);
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> PostOkAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    [Fact]
    public async Task A_standalone_procedure_is_reviewed_baselined_and_later_carried_once_requirements_are_on()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, WithoutRequirements);
        var introduce = Package(fixture, "SYSTPCR-000001", "SYSTP-000001", 0,
            TestProcedureChangeKind.Introduce, VerificationProcedureParentKind.Standalone);
        await SaveAsync(factory, introduce);
        await MemberSession.SignInAsync(client, "standalone.cm");

        // No change request exists to select, so the baseline freezes empty and records an empty requirement step.
        await PostOkAsync(client, $"/api/baselines/{fixture.BaselineId}/freeze", new { });
        await PostOkAsync(client, $"/api/baselines/{fixture.BaselineId}/materialize-requirements", new { });
        await PostOkAsync(client, $"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = introduce.Id });
        var materialized = await PostOkAsync(client, $"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });
        Assert.Equal(1, materialized.GetProperty("activeProcedureCount").GetInt32());
        Assert.Equal(0, materialized.GetProperty("coverageLinkCount").GetInt32());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var baseline = await db.CandidateBaselines.Include(x => x.Events).SingleAsync(x => x.Id == fixture.BaselineId);
            Assert.Contains(baseline.Events, x => x.Detail.Contains("because the project does not use Requirements"));
            var revision = await db.TestProcedureRevisions.SingleAsync();
            Assert.Equal(VerificationProcedureParentKind.Standalone, revision.ParentKind);
            Assert.Equal("", revision.DerivedRationale);
            Assert.Equal(fixture.BaselineId, revision.EffectiveBaselineId);
            Assert.Empty(await db.TestCoverage.ToListAsync());

            // Switching Requirements on later is allowed and rewrites nothing.
            var set = await db.ProjectFeatureSets.SingleAsync(x => x.ProjectId == fixture.ProjectId);
            set.Change(WithoutRequirements | ProjectFeature.Requirements, "test.setup", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            Assert.Equal(VerificationProcedureParentKind.Standalone,
                (await db.TestProcedureRevisions.AsNoTracking().SingleAsync()).ParentKind);
        }

        // The standalone procedure can still be modified as it is ...
        await SaveAsync(factory, Package(fixture, "SYSTPCR-000002", "SYSTP-000001", 1,
            TestProcedureChangeKind.Modify, VerificationProcedureParentKind.Standalone), approve: false);
        // ... but nothing new is Standalone in a project that has requirements to trace to.
        var refused = await Assert.ThrowsAsync<DomainException>(() => SaveAsync(factory, Package(fixture,
            "SYSTPCR-000003", "SYSTP-000002", 0, TestProcedureChangeKind.Introduce,
            VerificationProcedureParentKind.Standalone), approve: false));
        Assert.Contains("SYSTP-000002.00 cannot be Standalone: this project uses Requirements", refused.Message);

        // The same rule holds for a revision written directly, whatever the entry point.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var existing = await db.TestProcedures.SingleAsync();
            db.TestProcedureRevisions.Add(new TestProcedureRevision(existing.Id, 1, "Objective", "", "Steps",
                "Expected", TestProcedureState.Draft, "engineer", now,
                parentKind: VerificationProcedureParentKind.Standalone));
            await db.SaveChangesAsync();

            var fresh = new TestProcedure(fixture.ProjectId, "SYSTP-000003", "Other", "engineer", now,
                TestProcedureLevel.System);
            db.TestProcedures.Add(fresh);
            db.TestProcedureRevisions.Add(new TestProcedureRevision(fresh.Id, 0, "Objective", "", "Steps",
                "Expected", TestProcedureState.Draft, "engineer", now,
                parentKind: VerificationProcedureParentKind.Standalone));
            var direct = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Contains("SYSTP-000003.00 cannot be Standalone", direct.Message);
        }
    }

    [Fact]
    public async Task A_project_with_requirements_refuses_standalone_work_and_empty_baselines()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, ProjectFeatures.All);

        var refused = await Assert.ThrowsAsync<DomainException>(() => SaveAsync(factory, Package(fixture,
            "SYSTPCR-000001", "SYSTP-000001", 0, TestProcedureChangeKind.Introduce,
            VerificationProcedureParentKind.Standalone), approve: false));
        Assert.Contains("cannot be Standalone: this project uses Requirements", refused.Message);

        await MemberSession.SignInAsync(client, "standalone.cm");
        using var freeze = await client.PostAsJsonAsync($"/api/baselines/{fixture.BaselineId}/freeze", new { });
        Assert.Equal(HttpStatusCode.BadRequest, freeze.StatusCode);
        Assert.Contains("At least one approved change request or external package", await freeze.Content.ReadAsStringAsync());
    }
}
