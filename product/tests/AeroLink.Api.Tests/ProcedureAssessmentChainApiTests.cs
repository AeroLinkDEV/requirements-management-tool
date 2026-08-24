using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProcedureAssessmentChainApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid CaseReviewId);

    [Fact]
    public async Task Final_case_approval_raises_one_exact_unnumbered_procedure_assessment_and_work_allocates_once()
    {
        using var factory = new AeroLinkApiFactory(testLadderPolicy: ProcedureEnabledTestPolicy.Create());
        using var client = factory.CreateClient();
        var fixture = await SeedSubmittedCaseAsync(factory, "HighLevelSoftware");

        await LoginAsync(client, "case.reviewer");
        using (var approved = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.CaseReviewId}/approve", new
               {
                   password = AeroLinkApiFactory.MemberPassword,
                   rationale = "The exact Case change is complete and requires downstream Procedure assessment.",
                   meaning = "I approve this Case change-control package.",
               }))
        {
            Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
        }

        Guid assessmentId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var assessment = await db.TestChangeReviews.AsNoTracking().SingleAsync(x =>
                x.OriginKind == TestChangeReviewOriginKind.CaseReview
                && x.OriginReferenceId == fixture.CaseReviewId
                && x.ArtifactKind == VerificationArtifactKind.Procedure);
            assessmentId = assessment.Id;
            Assert.Equal(TestChangeReviewDiscipline.HighLevelSoftware, assessment.Discipline);
            Assert.Equal(TestChangeReviewState.Draft, assessment.State);
            Assert.Equal(TestChangeReviewOutcome.Pending, assessment.Outcome);
            Assert.Equal("", assessment.BaseNumber);
            Assert.Equal("HLRTCCR-727001.00", assessment.SourceCaseOriginNumber);
        }

        // A repeated approval request is refused by the ordinary lifecycle and cannot duplicate the exact
        // origin. The database uniqueness guard remains the second line of defence.
        using (var repeated = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.CaseReviewId}/approve", new
               {
                   password = AeroLinkApiFactory.MemberPassword,
                   rationale = "A retry must not raise a duplicate.",
                   meaning = "I approve this Case change-control package.",
               }))
            Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);

        await LoginAsync(client, "case.author");
        using var concluded = await client.PostAsJsonAsync($"/api/test-change-reviews/{assessmentId}/conclusion", new
        {
            testChangeRequired = true,
        });
        var body = await concluded.Content.ReadAsStringAsync();
        Assert.True(concluded.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        Assert.StartsWith("HLRTPCR-", json.RootElement.GetProperty("displayNumber").GetString(),
            StringComparison.Ordinal);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Single(await verifyDb.TestChangeReviews.AsNoTracking().Where(x =>
            x.OriginKind == TestChangeReviewOriginKind.CaseReview
            && x.OriginReferenceId == fixture.CaseReviewId
            && x.ArtifactKind == VerificationArtifactKind.Procedure).ToListAsync());
    }

    [Fact]
    public async Task Case_review_origin_no_work_conclusion_stays_unnumbered_and_creates_no_procedure_package()
    {
        using var factory = new AeroLinkApiFactory(testLadderPolicy: ProcedureEnabledTestPolicy.Create());
        using var client = factory.CreateClient();
        var fixture = await SeedSubmittedCaseAsync(factory, "HighLevelSoftware");

        await LoginAsync(client, "case.reviewer");
        using (var approved = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.CaseReviewId}/approve", new
               {
                   password = AeroLinkApiFactory.MemberPassword,
                   rationale = "The exact Case change is complete and needs a downstream Procedure assessment.",
                   meaning = "I approve this Case change-control package.",
               }))
            Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());

        Guid assessmentId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            assessmentId = await db.TestChangeReviews.AsNoTracking().Where(x =>
                    x.OriginKind == TestChangeReviewOriginKind.CaseReview
                    && x.OriginReferenceId == fixture.CaseReviewId
                    && x.ArtifactKind == VerificationArtifactKind.Procedure)
                .Select(x => x.Id).SingleAsync();
        }

        await LoginAsync(client, "case.author");
        using var concluded = await client.PostAsJsonAsync($"/api/test-change-reviews/{assessmentId}/conclusion", new
        {
            testChangeRequired = false,
            rationale = "The approved Procedure inventory already exercises both exact Case changes.",
        });
        var body = await concluded.Content.ReadAsStringAsync();
        Assert.True(concluded.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("NoChangeRequired", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("", json.RootElement.GetProperty("baseNumber").GetString());

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var assessment = await verifyDb.TestChangeReviews.AsNoTracking()
            .Include(x => x.ProcedureChanges).SingleAsync(x => x.Id == assessmentId);
        Assert.Equal(TestChangeReviewOutcome.NoChangeRequired, assessment.Outcome);
        Assert.Equal("", assessment.BaseNumber);
        Assert.Empty(assessment.ProcedureChanges);
        Assert.Single(await verifyDb.TestChangeReviews.AsNoTracking().Where(x =>
            x.OriginKind == TestChangeReviewOriginKind.CaseReview
            && x.OriginReferenceId == fixture.CaseReviewId
            && x.ArtifactKind == VerificationArtifactKind.Procedure).ToListAsync());
        Assert.Empty(await verifyDb.TestChangeReviews.AsNoTracking().Where(x =>
            x.OriginReferenceId == fixture.CaseReviewId
            && x.ArtifactKind == VerificationArtifactKind.Procedure
            && x.BaseNumber != "").ToListAsync());
    }

    [Fact]
    public async Task Case_only_profile_preserves_case_approval_without_raising_procedure_work()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedSubmittedCaseAsync(factory, "LowLevelSoftware");

        await LoginAsync(client, "case.reviewer");
        using var approved = await client.PostAsJsonAsync($"/api/test-change-reviews/{fixture.CaseReviewId}/approve", new
        {
            password = AeroLinkApiFactory.MemberPassword,
            rationale = "The LLR Case package is complete.",
            meaning = "I approve this LLR Case package under the Case-only profile.",
        });
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Empty(await db.TestChangeReviews.AsNoTracking()
            .Where(x => x.ArtifactKind == VerificationArtifactKind.Procedure
                && x.OriginKind == TestChangeReviewOriginKind.CaseReview).ToListAsync());
        Assert.Equal(TestChangeReviewState.Approved,
            (await db.TestChangeReviews.AsNoTracking().SingleAsync(x => x.Id == fixture.CaseReviewId)).State);
    }

    private static async Task<Fixture> SeedSubmittedCaseAsync(AeroLinkApiFactory factory, string disciplineName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var discipline = Enum.Parse<TestChangeReviewDiscipline>(disciplineName);
        var level = discipline == TestChangeReviewDiscipline.HighLevelSoftware
            ? RequirementLevel.HighLevel : RequirementLevel.LowLevel;
        var procedureLevel = discipline == TestChangeReviewDiscipline.HighLevelSoftware
            ? TestProcedureLevel.HighLevel : TestProcedureLevel.LowLevel;
        var prefix = discipline == TestChangeReviewDiscipline.HighLevelSoftware ? "HLR" : "LLR";
        var program = new ProgramRecord("Procedure assessment chain program", $"PA{prefix}");
        var project = new ProjectRecord(program.Id, "Software", "Procedure assessment chain project");
        var release = new SoftwareRelease(project.Id, "7.27", false);
        var author = new UserAccount("case.author", "Case Author", "case.author@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var reviewer = new UserAccount("case.reviewer", "Case Reviewer", "case.reviewer@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(program, project, release, author, reviewer,
            new ProgramMembership(author.Id, program.Id, ProgramRole.TestEngineer, "issue-727-test", now),
            new ProgramMembership(reviewer.Id, program.Id, ProgramRole.Approver, "issue-727-test", now));
        var source = new SystemChangeRequest($"{prefix}CR-727001", 0, project.Id, release.Id,
            "Case assessment source", "Problem", "Analysis", "Solution", author.UserName, now,
            ChangeRequestType.Software, softwareLevel: level);
        var key = new VerificationArtifactKey(VerificationArtifactProfile.ToNeutral(discipline),
            VerificationArtifactKind.Case);
        var review = new TestChangeReview(project.Id, release.Id, source.Id, key,
            $"{prefix}CR-727001.00", now, $"{prefix}TCCR-727001", authorId: author.UserName);
        review.RecordTestChangeRequired(author.UserName, now);
        review.WriteCase(author.UserName, "Approved Case change", "The Case behavior changed.",
            "The exact Case change is ready for downstream assessment.",
            "Approve the Case package and assess its Procedure relationship.", now);
        review.AddProcedureChange(author.UserName, new TestProcedureChangeDraft(
            $"{prefix}TC-727001", 0, procedureLevel, TestProcedureChangeKind.Introduce,
            "Controlled Case change", "Verify the Case behavior.", "Configured build.",
            "Exercise the Case.", "Expected Case behavior is observed.", "Introduce the exact Case.",
            ParentKind: VerificationProcedureParentKind.Derived,
            DerivedRationale: "This test fixture is an exact standalone Case."), now);
        review.AddProcedureChange(author.UserName, new TestProcedureChangeDraft(
            $"{prefix}TC-727002", 0, procedureLevel, TestProcedureChangeKind.Introduce,
            "Second controlled Case change", "Verify the second Case behavior.", "Configured build.",
            "Exercise the second Case.", "Expected second Case behavior is observed.",
            "Introduce a second exact Case within the same package.",
            ParentKind: VerificationProcedureParentKind.Derived,
            DerivedRationale: "This second test fixture Case is also standalone."), now);
        review.SubmitForReview(author.UserName,
            [new ApproverSelection(reviewer.UserName, reviewer.DisplayName, ProgramRole.Approver)],
            true, now.AddMinutes(1));
        db.AddRange(source, review);
        await db.SaveChangesAsync();
        return new(project.Id, release.Id, review.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName = user,
            password = AeroLinkApiFactory.MemberPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
