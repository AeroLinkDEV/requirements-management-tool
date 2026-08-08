using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Raising a test change request deliberately, alongside the ones raised automatically.
///
/// One appears whenever a change request is approved, so nothing goes unnoticed. That is not the only way
/// the work arrives: a verification engineer may decide a set of changes is best tested as a single package
/// of their own making, and the only way to express that was previously to let the automatic packages appear
/// and then fold them together.
/// </summary>
public sealed class ManualTestChangeRequestApiTests
{
    private sealed record Fixture(Guid ProjectId, Guid ReleaseId, Guid FirstChangeId, Guid SecondChangeId,
        Guid AutoRaisedChangeId, Guid AutoTcrId, Guid OtherBuildChangeId, Guid ProblemReportId,
        Guid OtherBuildProblemReportId);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Manual Program", "MAN");
        var project = new ProjectRecord(program.Id, "Software", "Manual Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        var otherBuild = new SoftwareRelease(project.Id, "1.7", false);
        db.AddRange(program, project, release, otherBuild);

        SystemChangeRequest Approved(string number, string requirement, Guid releaseId)
        {
            var scr = new SystemChangeRequest(number, 0, project.Id, releaseId, "Oceanic", "P", "A", "S", "author", now);
            scr.AddRequirementChange("author", requirement, 0, RequirementLevel.System, RequirementChangeKind.Introduce,
                "The FMS shall sequence oceanic waypoints.", "New capability", "Analysis", now);
            scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
            scr.ApproveActiveStage("reviewer", now);
            return scr;
        }

        var first = Approved("SRCR-00910", "SYSR-00000911", release.Id);
        var second = Approved("SRCR-00911", "SYSR-00000912", release.Id);
        var autoRaised = Approved("SRCR-00912", "SYSR-00000913", release.Id);
        var elsewhere = Approved("SRCR-00913", "SYSR-00000914", otherBuild.Id);
        var report = new ProblemReport(project.Id, "PR-00910", "Route sequencing disagreement",
            "The observed route differs from the approved plan.", "", "quality", now);
        var otherReport = new ProblemReport(project.Id, "PR-00911", "Future-build problem",
            "This problem belongs to another build.", "", "quality", now);
        db.AddRange(first, second, autoRaised, elsewhere, report, otherReport,
            new ProblemReportLink(report.Id, "Release", release.Id, "BuildScope", "quality", now),
            new ProblemReportLink(otherReport.Id, "Release", otherBuild.Id, "BuildScope", "quality", now));

        foreach (var (user, role) in new[]
                 {
                     ("manual.engineer", ProgramRole.TestEngineer),
                     ("manual.lead", ProgramRole.TestLead),
                     ("manual.outsider", ProgramRole.Engineer),
                 })
        {
            var account = new UserAccount(user, user, $"{user}@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(account);
            db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
        }
        await db.SaveChangesAsync();

        // Only this one gets an automatic package, so the others are genuinely unclaimed.
        var tracked = await db.SystemChangeRequests.Include(x => x.RequirementChanges).SingleAsync(x => x.Id == autoRaised.Id);
        await impact.RaiseForApprovedChangeRequestAsync(tracked, now, default);
        await db.SaveChangesAsync();
        var autoTcrId = await db.TestChangeReviews.Where(x => x.ChangeRequestId == autoRaised.Id)
            .Select(x => x.Id).SingleAsync();

        return new(project.Id, release.Id, first.Id, second.Id, autoRaised.Id, autoTcrId, elsewhere.Id,
            report.Id, otherReport.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task An_engineer_raises_a_package_covering_two_changes_at_once()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.FirstChangeId, fixture.SecondChangeId },
                problemReportIds = new[] { fixture.ProblemReportId },
                title = "Oceanic sequencing verification",
                problem = "Two approved changes touch the oceanic sequencing path.",
                analysis = "They share one procedure and are best tested as one package.",
                solution = "Raise one SYSTCR carrying both and verify the combined behavior." });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");

        var created = JsonSerializer.Deserialize<JsonElement>(body);
        // Numbered like any other controlled package, not marked out as hand-made.
        Assert.Matches(@"^SYSTCR-\d{6}\.\d{2}$", created.GetProperty("displayNumber").GetString()!);
        Assert.Equal(2, created.GetProperty("covered").EnumerateArray().Count());
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/releases/{fixture.ReleaseId}/test-change-reviews");
        var package = Assert.Single(list.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == created.GetProperty("id").GetGuid());
        Assert.Equal("PR-00910.00", Assert.Single(package.GetProperty("problemReports").EnumerateArray()).GetProperty("displayNumber").GetString());
        Assert.Equal("Oceanic sequencing verification", package.GetProperty("title").GetString());
        Assert.Equal("They share one procedure and are best tested as one package.", package.GetProperty("analysis").GetString());
        // DEC-102: raising the package is itself taking it on, so the creator holds it from the first moment.
        Assert.Equal("manual.engineer", package.GetProperty("assignedEngineerId").GetString());
    }

    [Fact]
    public async Task A_package_has_to_answer_for_something()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        // A package covering nothing has nothing to decide and would sit in the queue looking like work.
        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// An automatic assessment that has actually been concluded is a real package; a manual raise cannot
    /// quietly take its change away. Two packages answering for one change could be approved with
    /// contradictory procedure decisions, and nothing would notice.
    /// </summary>
    [Fact]
    public async Task An_assessed_change_is_refused_by_name()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.AutoTcrId);
            review.RecordTestChangeRequired("manual.engineer", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.AutoRaisedChangeId }, title = "Verification package" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Named, so the engineer knows where the change went. This one already has an assessment of its own
        // rather than being folded into somebody else's package, and the refusal says which it is — saying
        // it was "covered by" its own number would name the change after itself.
        Assert.Contains("SRCR-00912", body);
        Assert.Contains("already has a System test assessment", body);
    }

    [Fact]
    public async Task A_pending_automatic_review_becomes_the_manual_package()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.AutoRaisedChangeId },
                title = "Manual package over the pending automatic review" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
        var created = JsonSerializer.Deserialize<JsonElement>(body);
        var packageId = created.GetProperty("id").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            // One row, one ChangeRequestId, one Discipline, one Revision: the automatic review is the
            // package, concluded and numbered, not a second row that would fight it for the unique key.
            var manual = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.AutoTcrId);
            Assert.Equal(packageId, manual.Id);
            Assert.Equal(TestChangeReviewState.Open, manual.State);
            Assert.Matches("^SYSTCR-", manual.BaseNumber);
            Assert.Equal("Manual package over the pending automatic review", manual.Title);
            Assert.Equal("manual.engineer", manual.AssignedEngineerId);
            Assert.Contains(fixture.AutoRaisedChangeId, manual.CoveredChangeRequestIds);
        }
    }

    [Fact]
    public async Task Folded_changes_pending_automatic_reviews_are_superseded()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var impact = scope.ServiceProvider.GetRequiredService<VerificationImpactService>();
            var second = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .SingleAsync(x => x.Id == fixture.SecondChangeId);
            await impact.RaiseForApprovedChangeRequestAsync(second, DateTimeOffset.UtcNow, default);
            await db.SaveChangesAsync();
        }

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.FirstChangeId, fixture.SecondChangeId },
                title = "Manual package folding a pending automatic review" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var package = await db.TestChangeReviews.Include(x => x.AdditionalSources)
                .SingleAsync(x => x.ChangeRequestId == fixture.FirstChangeId
                    && x.Discipline == TestChangeReviewDiscipline.System && x.Revision == 0
                    && x.State != TestChangeReviewState.Superseded);
            Assert.Contains(fixture.SecondChangeId, package.CoveredChangeRequestIds);
            var secondAutomatic = await db.TestChangeReviews.SingleAsync(x => x.ChangeRequestId == fixture.SecondChangeId
                && x.Discipline == TestChangeReviewDiscipline.System && x.State == TestChangeReviewState.Superseded);
            Assert.Equal(package.Id, secondAutomatic.SupersededByTestChangeRequestId);
            Assert.Contains("raised manually", secondAutomatic.SupersededReason);
        }
    }

    [Fact]
    public async Task A_change_allocated_to_another_build_cannot_be_covered_here()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.OtherBuildChangeId }, title = "Verification package" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("change_request_not_selectable", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_problem_report_allocated_to_another_build_cannot_be_linked()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.FirstChangeId },
                problemReportIds = new[] { fixture.OtherBuildProblemReportId }, title = "Verification package" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("target build", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Problem_report_links_are_editable_only_while_the_test_change_request_is_open()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var open = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.AutoTcrId}/problem-reports",
            new { problemReportIds = new[] { fixture.ProblemReportId } });
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.SingleAsync(x => x.Id == fixture.AutoTcrId);
            // Automatically raised packages arrive unassessed now, and there is nothing to send for review
            // until somebody has said whether test work is required at all.
            review.RecordTestChangeRequired("manual.engineer", DateTimeOffset.UtcNow);
            review.Submit("manual.engineer", "independent.reviewer", true, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var inReview = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.AutoTcrId}/problem-reports",
            new { problemReportIds = new[] { fixture.ProblemReportId } });
        Assert.Equal(HttpStatusCode.Conflict, inReview.StatusCode);
        Assert.Contains("only while", await inReview.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Raising_one_takes_verification_authority()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.outsider");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.FirstChangeId }, title = "Verification package" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_test_lead_can_raise_a_package_too()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.lead");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Lead-raised verification package",
                problem = "P", analysis = "A", solution = "S" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
    }

    /// <summary>
    /// Revising a package takes its folded-in claims with it, over the route rather than in the aggregate.
    ///
    /// The domain moves the claims, but only over what it was given. The endpoint loads the package to revise
    /// it, and an unloaded collection is an empty one — so a missing Include would move nothing, leave the
    /// claim on the superseded revision, and report success. Nothing in the aggregate can catch that, which
    /// is why this drives the real route and then reads the claim row back.
    /// </summary>
    [Fact]
    public async Task Revising_a_package_carries_its_folded_in_change_requests_onto_the_successor()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var package = await db.TestChangeReviews.Include(x => x.AdditionalSources)
                .SingleAsync(x => x.Id == fixture.AutoTcrId);
            package.RecordTestChangeRequired("manual.engineer", now);
            package.IncludeChangeRequest("manual.engineer", fixture.FirstChangeId, "SRCR-00910.00", now);
            package.Submit("manual.engineer", "test.lead", true, now.AddMinutes(1));
            package.Approve("test.lead", "Sound.", now.AddMinutes(2));
            await db.SaveChangesAsync();
        }

        // The precondition, asserted rather than assumed: without a stored claim the move under test would
        // have nothing to move, and the failure would look identical to the endpoint losing it.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var before = await db.TestChangeRequestClaims.AsNoTracking()
                .SingleAsync(x => x.ChangeRequestId == fixture.FirstChangeId);
            Assert.Equal(fixture.AutoTcrId, before.TestChangeReviewId);
        }

        using var revised = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.AutoTcrId}/revise", new { });
        var body = await revised.Content.ReadAsStringAsync();
        Assert.True(revised.StatusCode == HttpStatusCode.OK, $"{(int)revised.StatusCode}: {body}");
        var next = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(1, next.GetProperty("revision").GetInt32());
        // The change it was raised from plus the one folded into it. One would mean the claim was left behind.
        Assert.Equal(2, next.GetProperty("coveredChangeRequests").GetInt32());
        var successorId = next.GetProperty("id").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var claims = await db.TestChangeRequestClaims.AsNoTracking()
                .Where(x => x.ChangeRequestId == fixture.FirstChangeId).ToListAsync();
            Assert.Equal(successorId, Assert.Single(claims).TestChangeReviewId);
        }
    }

    [Fact]
    public async Task A_manually_raised_package_requires_a_title()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.FirstChangeId } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("title", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_source_change_request_still_in_draft_cannot_be_covered()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        Guid draftId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var change = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == fixture.FirstChangeId);
            var draft = new SystemChangeRequest("SRCR-00920", 0, change.ProjectId, change.TargetReleaseId,
                "Still being drafted", "P", "A", "S", "author", DateTimeOffset.UtcNow);
            db.Add(draft);
            await db.SaveChangesAsync();
            draftId = draft.Id;
        }

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { draftId }, title = "Premature package" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("approved change requests", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_engineering_case_round_trips_and_can_be_corrected_while_open()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var created = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Rich case round trip",
                problem = "Problem plain",
                analysis = "Analysis plain",
                solution = "Solution plain",
                problemRich = "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Problem rich\"}]}",
                analysisRich = "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Analysis rich\"}]}",
                solutionRich = "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Solution rich\"}]}"
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var packageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var caseEdit = await client.PostAsJsonAsync($"/api/test-change-reviews/{packageId}/case",
            new
            {
                title = "Rich case corrected",
                problem = "Problem corrected",
                analysis = "Analysis corrected",
                solution = "Solution corrected",
                analysisRich = "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Analysis corrected rich\"}]}"
            });
        Assert.Equal(HttpStatusCode.OK, caseEdit.StatusCode);
        var corrected = await caseEdit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rich case corrected", corrected.GetProperty("title").GetString());
        // Rich content is authoritative: the plain projection is derived from it, never supplied beside it.
        Assert.Equal("Analysis corrected rich", corrected.GetProperty("analysis").GetString());
        Assert.Contains("Analysis corrected rich", corrected.GetProperty("analysisRich").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.SingleAsync(x => x.Id == packageId);
            review.Submit("manual.engineer", "independent.reviewer", true, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var inReview = await client.PostAsJsonAsync($"/api/test-change-reviews/{packageId}/case",
            new { title = "Too late", problem = "P", analysis = "A", solution = "S" });
        Assert.Equal(HttpStatusCode.BadRequest, inReview.StatusCode);
        Assert.Contains("cannot be edited", await inReview.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_authored_package_cannot_be_submitted_with_an_incomplete_case()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var created = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Title only",
                problem = "",
                analysis = "",
                solution = ""
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var packageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var submit = await client.PostAsJsonAsync($"/api/test-change-reviews/{packageId}/submit",
            new { approverId = "independent.reviewer" });
        Assert.Equal(HttpStatusCode.BadRequest, submit.StatusCode);
        Assert.Contains("Complete the test change request case", await submit.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unheld_package_case_cannot_be_edited_by_someone_else()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var created = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Held by its creator",
                problem = "P",
                analysis = "A",
                solution = "S"
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var packageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // A second test engineer without the lead role cannot rewrite the case of a package the creator holds.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var other = new UserAccount("manual.other", "Other Engineer", "other@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.Add(other);
            db.Add(new ProgramMembership(other.Id,
                await db.Programs.Where(x => x.Name == "Manual Program").Select(x => x.Id).SingleAsync(), ProgramRole.TestEngineer, "test.setup", now));
            await db.SaveChangesAsync();
        }
        await LoginAsync(client, "manual.other");

        using var response = await client.PostAsJsonAsync($"/api/test-change-reviews/{packageId}/case",
            new { title = "Stolen", problem = "P", analysis = "A", solution = "S" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HLR_and_LLR_packages_are_numbered_and_raised_independently()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        Guid hlrId, llrId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var hlr = new SystemChangeRequest("HLRCR-00920", 0, fixture.ProjectId, fixture.ReleaseId,
                "HLR change", "P", "A", "S", "author", now, ChangeRequestType.Software,
                softwareLevel: RequirementLevel.HighLevel);
            hlr.AddRequirementChange("author", "HLR-000001", 0, RequirementLevel.HighLevel,
                RequirementChangeKind.Introduce, "The HLR shall expose the new behavior.", "r", "v", now);
            hlr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
            hlr.ApproveActiveStage("reviewer", now);

            var llr = new SystemChangeRequest("LLRCR-00920", 0, fixture.ProjectId, fixture.ReleaseId,
                "LLR change", "P", "A", "S", "author", now, ChangeRequestType.Software,
                softwareLevel: RequirementLevel.LowLevel);
            llr.AddRequirementChange("author", "LLR-000001", 0, RequirementLevel.LowLevel,
                RequirementChangeKind.Introduce, "The LLR shall implement the new behavior.", "r", "v", now);
            llr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
            llr.ApproveActiveStage("reviewer", now);

            db.AddRange(hlr, llr);
            await db.SaveChangesAsync();
            hlrId = hlr.Id;
            llrId = llr.Id;
        }

        using var hlrResponse = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "HighLevelSoftware", changeRequestIds = new[] { hlrId }, title = "HLR package" });
        var hlrBody = await hlrResponse.Content.ReadAsStringAsync();
        Assert.True(hlrResponse.StatusCode == HttpStatusCode.Created, $"{(int)hlrResponse.StatusCode}: {hlrBody}");
        Assert.Matches(@"^HLRTCR-\d{6}\.\d{2}$",
            JsonSerializer.Deserialize<JsonElement>(hlrBody).GetProperty("displayNumber").GetString()!);

        using var llrResponse = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "LowLevelSoftware", changeRequestIds = new[] { llrId }, title = "LLR package" });
        var llrBody = await llrResponse.Content.ReadAsStringAsync();
        Assert.True(llrResponse.StatusCode == HttpStatusCode.Created, $"{(int)llrResponse.StatusCode}: {llrBody}");
        Assert.Matches(@"^LLRTCR-\d{6}\.\d{2}$",
            JsonSerializer.Deserialize<JsonElement>(llrBody).GetProperty("displayNumber").GetString()!);
    }
}
