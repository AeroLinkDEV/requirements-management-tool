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

        // A project without Problem Reports cannot hold one: the save boundary refuses it.
        var report = features.HasFlag(ProjectFeature.ProblemReports)
            ? new ProblemReport(project.Id, "PR-00001", "Bench rig drops frames",
                "Frames are lost under load.", "Analysis", "reporter", now, targetReleaseId: release.Id)
            : null;
        var baseline = new CandidateBaseline("SW-00.01", 0, project.Id, release.Id, null, "Standalone baseline", "cm", now);
        if (report is not null) db.Add(report);
        db.Add(baseline);
        UserAccount? configurationManager = null;
        foreach (var (user, role) in new[]
                 {
                     ("standalone.cm", ProgramRole.ConfigurationManager),
                     ("standalone.engineer", ProgramRole.TestEngineer),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
            if (role == ProgramRole.ConfigurationManager) configurationManager = account;
        }
        db.Add(new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
            configurationManager!.Id, "test.setup", now));
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, baseline.Id, report?.Id ?? Guid.Empty);
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
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
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

        // A package raised on its own case is refused whatever the entry point.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        db.Add(TestChangeReview.OnOwnCase(fixture.ProjectId, fixture.ReleaseId, SystemProcedure,
            DateTimeOffset.UtcNow, "SYSTPCR-000002", authorId: "engineer"));
        var ownCase = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        Assert.Contains("not on its own case", ownCase.Message);
    }

    private static readonly VerificationArtifactKey SystemProcedure =
        new(VerificationDiscipline.System, VerificationArtifactKind.Procedure);

    private static object OwnCaseRequest(string baseNumber) => new
    {
        discipline = "System",
        changeRequestIds = Array.Empty<Guid>(),
        title = "Bench frame capture",
        problem = "The rig loses frames under load.",
        analysis = "Nothing exercises it.",
        solution = "Add a procedure.",
        artifactChanges = new[]
        {
            new
            {
                baseNumber, revision = 0, level = "System", kind = "Introduce", title = "Frame capture under load",
                objective = "Show the rig keeps every frame at load.", preconditions = "Rig powered.",
                steps = "1. Apply load. 2. Count frames.", expectedResult = "No frame is lost.",
                rationale = "The rig loses frames.", parentKind = "Standalone",
            },
        },
    };

    [Fact]
    public async Task Test_work_is_raised_on_its_own_case_where_there_is_nothing_else_to_raise_it_from()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        // Neither Requirements nor Problem Reports: no change request or report exists to raise work from.
        var fixture = await SeedAsync(factory, ProjectFeature.Verification | ProjectFeature.Release | ProjectFeature.TeamWork);
        await MemberSession.SignInAsync(client, "standalone.engineer");

        var created = await PostOkAsync(client, $"/api/releases/{fixture.ReleaseId}/test-change-requests",
            OwnCaseRequest("SYSTP-000001"));
        var id = created.GetProperty("id").GetGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges).SingleAsync(x => x.Id == id);
            Assert.Equal(TestChangeReviewOriginKind.OwnCase, review.OriginKind);
            Assert.Equal(review.Id, review.OriginReferenceId);
            Assert.Null(review.ChangeRequestId);
            Assert.Null(review.OriginatingProblemReportId);
            Assert.Equal(VerificationProcedureParentKind.Standalone, Assert.Single(review.ProcedureChanges).ParentKind);
        }
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/test-change-reviews/{id}/procedure-changes");
        Assert.Equal("Own case", detail.GetProperty("originDisplayLabel").GetString());

        // An authorless package still reaches Team Work, named by its origin.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.Add(TestChangeReview.OnOwnCase(fixture.ProjectId, fixture.ReleaseId, SystemProcedure,
                DateTimeOffset.UtcNow, "SYSTPCR-000099"));
            await db.SaveChangesAsync();
        }
        using (var board = await client.GetAsync($"/api/team-work?projectId={fixture.ProjectId}"))
        {
            var text = await board.Content.ReadAsStringAsync();
            Assert.True(board.StatusCode == HttpStatusCode.OK, text);
            Assert.Contains("\"raisedByKind\":\"ownCase\"", text);
        }

        // Its next revision keeps the one origin, even once Requirements is switched on ...
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges).SingleAsync(x => x.Id == id);
            review.Submit("standalone.engineer", "test.lead", true, now);
            review.Approve("test.lead", "Reviewed.", now);
            (await db.ProjectFeatureSets.SingleAsync(x => x.ProjectId == fixture.ProjectId))
                .Change(ProjectFeatures.All, "test.setup", now);
            await db.SaveChangesAsync();

            // The approved package's decision carries into the revision it produces ...
            var procedure = new TestProcedure(fixture.ProjectId, "SYSTP-000001", "Frame capture", "engineer", now,
                TestProcedureLevel.System);
            db.TestProcedures.Add(procedure);
            db.TestProcedureRevisions.Add(new TestProcedureRevision(procedure.Id, 0, "Objective", "", "Steps",
                "Expected", TestProcedureState.Draft, "engineer", now, sourceTestChangeRequestId: id,
                parentKind: VerificationProcedureParentKind.Standalone));
            await db.SaveChangesAsync();
            // ... but only for the artifact that package decided.
            var other = new TestProcedure(fixture.ProjectId, "SYSTP-000004", "Other", "engineer", now,
                TestProcedureLevel.System);
            db.TestProcedures.Add(other);
            db.TestProcedureRevisions.Add(new TestProcedureRevision(other.Id, 0, "Objective", "", "Steps",
                "Expected", TestProcedureState.Draft, "engineer", now, sourceTestChangeRequestId: id,
                parentKind: VerificationProcedureParentKind.Standalone));
            Assert.Contains("SYSTP-000004.00 cannot be Standalone",
                (await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync())).Message);
            db.ChangeTracker.Clear();
            review = await db.TestChangeReviews.Include(x => x.ProcedureChanges).SingleAsync(x => x.Id == id);

            var next = review.StartNextRevision("standalone.engineer", now, targetReleaseIsReleased: false);
            db.Add(next);
            await db.SaveChangesAsync();
            Assert.Equal(TestChangeReviewOriginKind.OwnCase, next.OriginKind);
            Assert.Equal(id, next.OriginReferenceId);

            // ... while a "later revision" that names no real first revision is refused.
            db.Add(TestChangeReview.OnOwnCase(fixture.ProjectId, fixture.ReleaseId, SystemProcedure,
                now, "SYSTPCR-000098", 1, "standalone.engineer", Guid.NewGuid()));
            var forged = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Contains("must name that package's first revision", forged.Message);
        }

        // With Requirements on, the endpoint again asks what the package answers for.
        using var refused = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            OwnCaseRequest("SYSTP-000002"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("test_change_request_needs_a_driver", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_requirements_coverage_and_readiness_show_each_cases_execution_status()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, WithoutRequirements);
        var introduce = Package(fixture, "SYSTPCR-000001", "SYSTP-000001", 0,
            TestProcedureChangeKind.Introduce, VerificationProcedureParentKind.Standalone);
        await SaveAsync(factory, introduce);
        await MemberSession.SignInAsync(client, "standalone.cm");
        await PostOkAsync(client, $"/api/baselines/{fixture.BaselineId}/freeze", new { });
        await PostOkAsync(client, $"/api/baselines/{fixture.BaselineId}/materialize-requirements", new { });
        await PostOkAsync(client, $"/api/baselines/{fixture.BaselineId}/test-change-requests",
            new { testChangeRequestId = introduce.Id });
        await PostOkAsync(client, $"/api/baselines/{fixture.BaselineId}/materialize-test-procedures", new { });

        async Task<JsonElement> CoverageAsync()
        {
            var coverage = await client.GetFromJsonAsync<JsonElement>(
                $"/api/verification-coverage?projectId={fixture.ProjectId}&baselineId={fixture.BaselineId}");
            Assert.False(coverage.GetProperty("requirementsInUse").GetBoolean());
            Assert.Equal(0, coverage.GetProperty("total").GetInt32());
            return coverage.GetProperty("executionStatus");
        }
        var notRun = await CoverageAsync();
        Assert.Equal(1, notRun.GetProperty("total").GetInt32());
        Assert.Equal(1, notRun.GetProperty("notRun").GetInt32());
        var item = Assert.Single(notRun.GetProperty("items").EnumerateArray());
        Assert.Equal("SYSTP-000001.00", item.GetProperty("displayNumber").GetString());
        Assert.Equal("NotRun", item.GetProperty("status").GetString());

        Guid campaignId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var campaign = new Domain.Releases.ReleaseCampaign(fixture.ProjectId, fixture.ReleaseId, fixture.BaselineId,
                "Bench 1.0", "standalone.cm", DateTimeOffset.UtcNow);
            db.Add(campaign);
            await db.SaveChangesAsync();
            campaignId = campaign.Id;
            var readiness = await scope.ServiceProvider.GetRequiredService<ReleaseReadinessService>()
                .CalculateAsync(campaignId, CancellationToken.None);
            var gates = readiness.Gates.ToDictionary(x => x.Code);
            foreach (var code in new[] { "change_control", "impact_disposition", "traceability", "verification_impact", "code_traceability" })
            {
                Assert.Equal("NotApplicable", gates[code].EvaluationState);
                Assert.True(gates[code].Complete);
                Assert.Contains("does not use Requirements", gates[code].Detail);
            }
            Assert.Equal("Baseline frozen and materialized", gates["baseline"].Name);
            Assert.True(gates["baseline"].Complete);
            Assert.Contains("no requirement specification is owed", gates["documents"].Detail);
            Assert.Equal(0, gates["documents"].Completed);
            Assert.True(gates["documents"].Total > 0);
            Assert.Equal("Every case has passed", gates["coverage"].Name);
            Assert.False(gates["coverage"].Complete);
            Assert.Contains("0 passed, 0 failed, 0 blocked, 1 not run", gates["coverage"].Detail);

            // A passing result for the build moves the case, and the gate, to Passed.
            var revision = await db.TestProcedureRevisions.SingleAsync();
            db.Add(new TestExecution(fixture.ProjectId, revision.Id, null, null, TestOutcome.Pass, "standalone.cm",
                "Bench rig A", "Every frame was kept.", "bench-log-001", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                fixture.ReleaseId));
            await db.SaveChangesAsync();
            readiness = await scope.ServiceProvider.GetRequiredService<ReleaseReadinessService>()
                .CalculateAsync(campaignId, CancellationToken.None);
            var coverageGate = readiness.Gates.Single(x => x.Code == "coverage");
            Assert.True(coverageGate.Complete);
            Assert.Equal(1, coverageGate.Completed);
        }
        var passed = await CoverageAsync();
        Assert.Equal(1, passed.GetProperty("passed").GetInt32());
        Assert.Equal("Passed", Assert.Single(passed.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task With_requirements_coverage_reports_no_execution_status()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory, ProjectFeatures.All);
        await MemberSession.SignInAsync(client, "standalone.cm");
        var coverage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/verification-coverage?projectId={fixture.ProjectId}&baselineId={fixture.BaselineId}");
        Assert.True(coverage.GetProperty("requirementsInUse").GetBoolean());
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("executionStatus").ValueKind);
    }
}
