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
        Guid OtherBuildProblemReportId, Guid AutoItemId, Guid HighLevelChangeId, Guid LowLevelChangeId);

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

        // Software changes at each level, so "which level drives this package" has a right and a wrong answer
        // to assert against. A software change must name its level; the domain refuses one without.
        SystemChangeRequest ApprovedSoftware(string number, string requirement, RequirementLevel level)
        {
            var scr = new SystemChangeRequest(number, 0, project.Id, release.Id, "Oceanic", "P", "A", "S", "author", now,
                type: ChangeRequestType.Software, softwareLevel: level);
            scr.AddRequirementChange("author", requirement, 0, level, RequirementChangeKind.Introduce,
                "The FMS software shall sequence oceanic waypoints.", "New capability", "Analysis", now);
            scr.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
            scr.ApproveActiveStage("reviewer", now);
            return scr;
        }

        var first = Approved("SRCR-00910", "SYSR-00000911", release.Id);
        var second = Approved("SRCR-00911", "SYSR-00000912", release.Id);
        var autoRaised = Approved("SRCR-00912", "SYSR-00000913", release.Id);
        var elsewhere = Approved("SRCR-00913", "SYSR-00000914", otherBuild.Id);
        var highLevel = ApprovedSoftware("HLRCR-00910", "HLR-00000911", RequirementLevel.HighLevel);
        var lowLevel = ApprovedSoftware("LLRCR-00910", "LLR-00000911", RequirementLevel.LowLevel);
        var report = new ProblemReport(project.Id, "PR-00910", "Route sequencing disagreement",
            "The observed route differs from the approved plan.", "", "quality", now);
        var otherReport = new ProblemReport(project.Id, "PR-00911", "Future-build problem",
            "This problem belongs to another build.", "", "quality", now);
        db.AddRange(first, second, autoRaised, elsewhere, highLevel, lowLevel, report, otherReport,
            new ProblemReportLink(report.Id, "Release", release.Id, "BuildScope", "quality", now),
            new ProblemReportLink(otherReport.Id, "Release", otherBuild.Id, "BuildScope", "quality", now));

        foreach (var (user, role) in new[]
                 {
                     ("manual.engineer", ProgramRole.TestEngineer),
                     ("manual.lead", ProgramRole.TestLead),
                     ("manual.reviewer", ProgramRole.Approver),
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
        var autoItemId = await db.VerificationImpactItems.Where(x => x.ChangeRequestId == autoRaised.Id)
            .Select(x => x.Id).SingleAsync();

        return new(project.Id, release.Id, first.Id, second.Id, autoRaised.Id, autoTcrId, elsewhere.Id,
            report.Id, otherReport.Id, autoItemId, highLevel.Id, lowLevel.Id);
    }

    private static async Task LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static void AddProcedureDecision(TestChangeReview review, string actor, DateTimeOffset now) =>
        review.AddProcedureChange(actor, new TestProcedureChangeDraft("SYSTP-009999", 0,
            TestProcedureLevel.System, TestProcedureChangeKind.Introduce, "Test fixture procedure",
            "Verify the package behavior.", "The target build is available.", "Exercise the change.",
            "The changed behavior is observed.", "The current package must name its procedure work.",
            JsonSerializer.Serialize(new[] { Guid.NewGuid() })), now);

    /// <summary>
    /// A procedure verifies the requirements one level above it, so the package that controls HLR test work
    /// answers for HLR requirement changes and nothing else. Before this the picker offered every approved
    /// change in the build, so raising an HLRTCR presented SRCRs and LLRCRs as choices — neither of which can
    /// drive an HLR procedure.
    /// </summary>
    [Theory]
    [InlineData("HighLevelSoftware", "HLRCR-00910")]
    [InlineData("LowLevelSoftware", "LLRCR-00910")]
    [InlineData("System", "SRCR-00910")]
    public async Task The_picker_offers_only_changes_at_the_level_the_package_controls(string discipline, string expected)
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.GetAsync(
            $"/api/releases/{fixture.ReleaseId}/test-change-request-sources?discipline={discipline}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var offered = (await response.Content.ReadFromJsonAsync<SourceChoice[]>())!;

        // The display number carries its revision suffix (HLRCR-00910.00), so the base number is matched.
        Assert.Contains(offered, x => x.DisplayNumber.StartsWith(expected, StringComparison.Ordinal));
        // Every offer is at the package's own level: nothing from a level above or below appears at all.
        var prefix = discipline switch
        {
            "HighLevelSoftware" => "HLRCR-",
            "LowLevelSoftware" => "LLRCR-",
            _ => "SRCR-",
        };
        Assert.All(offered, x => Assert.StartsWith(prefix, x.DisplayNumber));
    }

    /// <summary>
    /// Enforced on the server as well as in the picker. A filtered browser list is a convenience; a request
    /// that never opened the picker has to meet the same rule.
    /// </summary>
    [Fact]
    public async Task A_change_from_another_level_is_refused_even_when_named_directly()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "HighLevelSoftware", changeRequestIds = new[] { fixture.LowLevelChangeId },
                title = "Wrong level package",
                problem = "P", analysis = "A", solution = "S" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("change_request_wrong_level", body);
        Assert.Contains("LLRCR-00910", body);
    }

    /// <summary>
    /// An anomaly found in the field is a legitimate reason to write, correct or withdraw a procedure. A build
    /// may also carry no approved change at the package's own level, in which case requiring one made the
    /// package impossible to raise at all.
    /// </summary>
    [Fact]
    public async Task A_problem_report_alone_can_raise_a_package()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "HighLevelSoftware", changeRequestIds = Array.Empty<Guid>(),
                problemReportIds = new[] { fixture.ProblemReportId },
                title = "Corrective verification for a reported anomaly",
                problem = "P", analysis = "A", solution = "S" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var review = await db.TestChangeReviews.AsNoTracking().SingleAsync(x => x.Id == id);

        // Raised from the report, and from nothing else — the report holds the originating slot rather than
        // being a decoration alongside an invented change request.
        Assert.Null(review.ChangeRequestId);
        Assert.Equal(fixture.ProblemReportId, review.OriginatingProblemReportId);
        Assert.Equal("PR-00910.00", review.SourceProblemReportNumber);
        Assert.Empty(review.CoveredChangeRequestIds);
        // It is still a numbered controlled package.
        Assert.StartsWith("HLRTCR-", review.DisplayNumber);
    }

    /// <summary>
    /// Procedure decisions are saved with the package, the way a change request is created together with the
    /// requirement changes it proposes. A package created without them would be one proposal in two halves,
    /// the second of which somebody has to remember to write.
    /// </summary>
    [Fact]
    public async Task Procedure_decisions_are_saved_with_the_package_that_proposes_them()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Oceanic sequencing verification",
                problem = "P", analysis = "A", solution = "S",
                procedureChanges = new[]
                {
                    new
                    {
                        baseNumber = "SYSTP-009801", revision = 0, level = "System", kind = "Introduce",
                        title = "Verify oceanic sequencing", objective = "Show the sequencing holds.",
                        preconditions = "The build is available.", steps = "Exercise the changed behaviour.",
                        expectedResult = "The sequencing is observed to hold.",
                        rationale = "The approved change introduces behaviour with no procedure.",
                    },
                },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var review = await db.TestChangeReviews.AsNoTracking().Include(x => x.ProcedureChanges)
            .SingleAsync(x => x.Id == id);

        var change = Assert.Single(review.ProcedureChanges);
        Assert.Equal("SYSTP-009801", change.BaseNumber);
        Assert.Equal(TestProcedureChangeKind.Introduce, change.Kind);
        Assert.Equal("Verify oceanic sequencing", change.Title);
    }

    /// <summary>
    /// A malformed decision fails the whole create rather than leaving a package behind with the bad half
    /// silently dropped.
    /// </summary>
    [Fact]
    public async Task A_malformed_procedure_decision_refuses_the_whole_package()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Oceanic sequencing verification",
                problem = "P", analysis = "A", solution = "S",
                procedureChanges = new[]
                {
                    new
                    {
                        baseNumber = "", revision = 0, level = "System", kind = "Introduce",
                        title = "", objective = "", preconditions = "", steps = "",
                        expectedResult = "", rationale = "",
                    },
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // Nothing was written: no half-created package survives the refusal.
        Assert.False(await db.TestChangeReviews.AsNoTracking()
            .AnyAsync(x => x.Title == "Oceanic sequencing verification"));
    }

    /// <summary>A package still has to say what concluded the work was required.</summary>
    [Fact]
    public async Task A_package_raised_from_nothing_at_all_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "HighLevelSoftware", changeRequestIds = Array.Empty<Guid>(),
                problemReportIds = Array.Empty<Guid>(),
                title = "Package with no driver", problem = "P", analysis = "A", solution = "S" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("test_change_request_needs_a_driver", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The evidence contract is a hashed snapshot, so a package raised from a change request has to serialize
    /// exactly as it did before Problem Report origins existed — otherwise every hash already recorded
    /// against a signature would stop verifying.
    /// </summary>
    [Fact]
    public async Task A_change_request_origin_still_records_itself_as_the_originating_source()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "System", changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Ordinary package", problem = "P", analysis = "A", solution = "S" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var review = await db.TestChangeReviews.AsNoTracking().SingleAsync(x => x.Id == id);

        Assert.Equal(fixture.FirstChangeId, review.ChangeRequestId);
        Assert.Null(review.OriginatingProblemReportId);
        Assert.Equal("", review.SourceProblemReportNumber);
        Assert.Contains(fixture.FirstChangeId, review.CoveredChangeRequestIds);
    }

    /// <summary>The matching level is accepted, so the refusal above is about the level and nothing else.</summary>
    [Fact]
    public async Task A_change_at_the_matching_level_raises_the_package()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        using var response = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new { discipline = "HighLevelSoftware", changeRequestIds = new[] { fixture.HighLevelChangeId },
                title = "High-level verification package",
                problem = "P", analysis = "A", solution = "S" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private sealed record SourceChoice(Guid ChangeRequestId, string DisplayNumber, string Title, string State,
        bool Selectable, string? Reason);

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
            review.WriteCase("manual.engineer", "Verification case", "Problem", "Analysis", "Solution", DateTimeOffset.UtcNow);
            AddProcedureDecision(review, "manual.engineer", DateTimeOffset.UtcNow);
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
            package.WriteCase("manual.engineer", "Verification case", "Problem", "Analysis", "Solution", now);
            AddProcedureDecision(package, "manual.engineer", now);
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
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
                .SingleAsync(x => x.Id == packageId);
            AddProcedureDecision(review, "manual.engineer", DateTimeOffset.UtcNow);
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
        var body = await submit.Content.ReadAsStringAsync();
        Assert.Contains("Complete the test change request case", body);
        Assert.Contains("test_change_request_case_incomplete", body);
        Assert.Contains("Problem", body);
        Assert.Contains("Analysis", body);
        Assert.Contains("Solution", body);
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

    [Fact]
    public async Task A_stale_case_edit_is_refused_without_writing()
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
                title = "Original title",
                problem = "P", analysis = "A", solution = "S"
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var packageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var workspace = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{packageId}/procedure-changes");
        var version = workspace.GetProperty("version").GetInt64();

        using var stale = await client.PostAsJsonAsync($"/api/test-change-reviews/{packageId}/case",
            new { title = "Stale title", problem = "P", analysis = "A", solution = "S",
                expectedVersion = version + 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("stale_version", await stale.Content.ReadAsStringAsync());

        var after = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{packageId}/procedure-changes");
        Assert.Equal("Original title", after.GetProperty("title").GetString());
        Assert.Equal(version, after.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task A_stale_procedure_proposal_is_refused_without_writing()
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
                title = "Original title",
                problem = "P", analysis = "A", solution = "S"
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var packageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var version = (await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{packageId}/procedure-changes")).GetProperty("version").GetInt64();

        using var stale = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{packageId}/procedure-changes",
            new
            {
                kind = "Introduce", revision = 0, title = "Proposal", objective = "Objective",
                preconditions = "", steps = "Steps", expectedResult = "Expected", rationale = "Why",
                drivingRequirementRevisionIds = Array.Empty<Guid>(),
                expectedVersion = version + 1
            });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("stale_version", await stale.Content.ReadAsStringAsync());

        var after = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{packageId}/procedure-changes");
        Assert.Empty(after.GetProperty("procedureChanges").EnumerateArray());
    }

    [Fact]
    public async Task The_source_picker_reports_availability_honestly()
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

        var sources = await client.GetFromJsonAsync<JsonElement>(
            $"/api/releases/{fixture.ReleaseId}/test-change-request-sources?discipline=System");
        var items = sources.EnumerateArray().ToList();
        var first = items.Single(x => x.GetProperty("displayNumber").GetString()!.StartsWith("SRCR-00910"));
        Assert.True(first.GetProperty("selectable").GetBoolean());
        var assessed = items.Single(x => x.GetProperty("displayNumber").GetString()!.StartsWith("SRCR-00912"));
        Assert.False(assessed.GetProperty("selectable").GetBoolean());
        Assert.Contains("test assessment", assessed.GetProperty("reason").GetString());

        // A change folded into a package is reported as claimed by that package.
        using var created = await client.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Source picker package",
                problem = "P", analysis = "A", solution = "S"
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var packageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var folded = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{packageId}/change-requests",
            new { changeRequestId = fixture.SecondChangeId });
        Assert.True(folded.IsSuccessStatusCode, await folded.Content.ReadAsStringAsync());

        var updated = await client.GetFromJsonAsync<JsonElement>(
            $"/api/releases/{fixture.ReleaseId}/test-change-request-sources?discipline=System");
        var second = updated.EnumerateArray().Single(x =>
            x.GetProperty("displayNumber").GetString()!.StartsWith("SRCR-00911"));
        Assert.False(second.GetProperty("selectable").GetBoolean());
        Assert.Contains("Already covered by", second.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_true_two_context_case_race_returns_stale_version()
    {
        using var factory = new AeroLinkApiFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(clientA, "manual.engineer");
        await LoginAsync(clientB, "manual.engineer");

        using var created = await clientA.PostAsJsonAsync($"/api/releases/{fixture.ReleaseId}/test-change-requests",
            new
            {
                discipline = "System",
                changeRequestIds = new[] { fixture.FirstChangeId },
                title = "Race title",
                problem = "P", analysis = "A", solution = "S"
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var packageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var versionA = (await clientA.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{packageId}/procedure-changes")).GetProperty("version").GetInt64();
        var versionB = (await clientB.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{packageId}/procedure-changes")).GetProperty("version").GetInt64();
        Assert.Equal(versionA, versionB);

        using var winner = await clientA.PostAsJsonAsync($"/api/test-change-reviews/{packageId}/case",
            new { title = "Winner title", problem = "P", analysis = "A", solution = "S", expectedVersion = versionA });
        Assert.True(winner.IsSuccessStatusCode, await winner.Content.ReadAsStringAsync());

        using var loser = await clientB.PostAsJsonAsync($"/api/test-change-reviews/{packageId}/case",
            new { title = "Loser title", problem = "P", analysis = "A", solution = "S", expectedVersion = versionB });
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        var body = await loser.Content.ReadAsStringAsync();
        Assert.Contains("stale_version", body);

        var after = await clientB.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{packageId}/procedure-changes");
        Assert.Equal("Winner title", after.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_stale_conclusion_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client, "manual.engineer");

        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/releases/{fixture.ReleaseId}/test-change-reviews");
        var pending = list.GetProperty("items").EnumerateArray().Single(x =>
            x.GetProperty("id").GetGuid() == fixture.AutoTcrId);
        var version = pending.GetProperty("version").GetInt64();

        using var stale = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.AutoTcrId}/conclusion",
            new { testChangeRequired = true, rationale = "", expectedVersion = version + 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("stale_version", await stale.Content.ReadAsStringAsync());

        using var current = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.AutoTcrId}/conclusion",
            new { testChangeRequired = true, rationale = "", expectedVersion = version });
        Assert.True(current.IsSuccessStatusCode, await current.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_stale_problem_report_link_replacement_is_refused()
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
                title = "Link race",
                problem = "P", analysis = "A", solution = "S"
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var packageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var version = (await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{packageId}/procedure-changes")).GetProperty("version").GetInt64();

        using var stale = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{packageId}/problem-reports",
            new { problemReportIds = new[] { fixture.ProblemReportId }, expectedVersion = version + 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("stale_version", await stale.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_stale_procedure_withdrawal_is_refused()
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
                title = "Withdraw race",
                problem = "P", analysis = "A", solution = "S"
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var packageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Guid changeId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
                .SingleAsync(x => x.Id == packageId);
            AddProcedureDecision(review, "manual.engineer", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            changeId = review.ProcedureChanges.Single().Id;
        }
        var currentVersion = (await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{packageId}/procedure-changes")).GetProperty("version").GetInt64();

        using var stale = await client.DeleteAsync(
            $"/api/test-change-reviews/{packageId}/procedure-changes/{changeId}?expectedVersion={currentVersion + 1}");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("stale_version", await stale.Content.ReadAsStringAsync());

        using var current = await client.DeleteAsync(
            $"/api/test-change-reviews/{packageId}/procedure-changes/{changeId}?expectedVersion={currentVersion}");
        Assert.True(current.IsSuccessStatusCode, await current.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> PrepareSubmittableNoChangePackageAsync(AeroLinkApiFactory factory,
        HttpClient client, Guid autoTcrId, Guid itemId)
    {
        await LoginAsync(client, "manual.engineer");
        using var resolved = await client.PostAsJsonAsync($"/api/verification-impact/{itemId}/resolve",
            new { outcome = "NoTestRequired", rationale = "Existing procedures already cover this wording." });
        Assert.True(resolved.IsSuccessStatusCode, await resolved.Content.ReadAsStringAsync());
        using var concluded = await client.PostAsJsonAsync($"/api/test-change-reviews/{autoTcrId}/conclusion",
            new { testChangeRequired = false, rationale = "Existing procedures already exercise this wording." });
        Assert.True(concluded.IsSuccessStatusCode, await concluded.Content.ReadAsStringAsync());
        return autoTcrId;
    }

    private static async Task<long> CurrentVersionAsync(HttpClient client, Guid releaseId, Guid reviewId)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/releases/{releaseId}/test-change-reviews");
        return list.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == reviewId).GetProperty("version").GetInt64();
    }

    [Fact]
    public async Task A_problem_report_link_versus_submit_race_has_one_winner()
    {
        using var factory = new AeroLinkApiFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var reviewId = await PrepareSubmittableNoChangePackageAsync(factory, clientA, fixture.AutoTcrId, fixture.AutoItemId);
        Assert.NotEqual(Guid.Empty, reviewId);
        await LoginAsync(clientB, "manual.engineer");

        var versionA = await CurrentVersionAsync(clientA, fixture.ReleaseId, reviewId);
        var versionB = await CurrentVersionAsync(clientB, fixture.ReleaseId, reviewId);
        Assert.Equal(versionA, versionB);

        using var submitted = await clientB.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/submit",
            new { approverId = "manual.reviewer", expectedVersion = versionB });
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());

        using var link = await clientA.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/problem-reports",
            new { problemReportIds = new[] { fixture.ProblemReportId }, expectedVersion = versionA });
        Assert.Equal(HttpStatusCode.Conflict, link.StatusCode);
        Assert.Contains("stale_version", await link.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Empty(await db.ProblemReportLinks.Where(x =>
                x.ArtifactType == "TestChangeRequest" && x.ArtifactId == reviewId).ToListAsync());
        }
    }

    [Fact]
    public async Task An_impact_decision_change_versus_submit_race_has_one_winner()
    {
        using var factory = new AeroLinkApiFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var reviewId = await PrepareSubmittableNoChangePackageAsync(factory, clientA, fixture.AutoTcrId, fixture.AutoItemId);
        await LoginAsync(clientB, "manual.engineer");
        var versionBefore = await CurrentVersionAsync(clientA, fixture.ReleaseId, reviewId);

        // A reopens the decision while the package is still Open; that is a governed content change and
        // advances the package version.
        using var reopened = await clientA.PostAsJsonAsync($"/api/verification-impact/{fixture.AutoItemId}/reopen",
            new { rationale = "Rework the decision before submission." });
        Assert.True(reopened.IsSuccessStatusCode, await reopened.Content.ReadAsStringAsync());

        using var staleSubmit = await clientB.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/submit",
            new { approverId = "manual.reviewer", expectedVersion = versionBefore });
        Assert.Equal(HttpStatusCode.Conflict, staleSubmit.StatusCode);
        Assert.Contains("stale_version", await staleSubmit.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_true_link_versus_submit_collision_when_the_link_commits_first()
    {
        using var factory = new AeroLinkApiFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var reviewId = await PrepareSubmittableNoChangePackageAsync(factory, clientA, fixture.AutoTcrId, fixture.AutoItemId);
        await LoginAsync(clientB, "manual.engineer");
        var version = await CurrentVersionAsync(clientA, fixture.ReleaseId, reviewId);

        using var gate = new SaveRaceGate(factory.ConnectionString);
        try
        {
            // Both requests load Version N before either saves; the link's save is released first and
            // commits, so the submit's save loses on the concurrency token.
            var linkTask = clientA.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/problem-reports",
                new { problemReportIds = new[] { fixture.ProblemReportId }, expectedVersion = version });
            Assert.True(await gate.FirstEnteredAsync(TimeSpan.FromSeconds(30)),
                "The link request never reached SaveChanges.");
            var submitTask = clientB.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/submit",
                new { approverId = "manual.reviewer", expectedVersion = version });
            Assert.True(await gate.SecondEnteredAsync(TimeSpan.FromSeconds(30)),
                "The submit request never reached SaveChanges.");

            gate.ReleaseFirst();
            using var link = await linkTask;
            Assert.True(link.IsSuccessStatusCode, await link.Content.ReadAsStringAsync());

            gate.ReleaseSecond();
            using var submit = await submitTask;
            var submitBody = await submit.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Conflict, submit.StatusCode);
            Assert.Contains("stale_version", submitBody);
        }
        finally
        {
            gate.Dispose();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.True(await db.ProblemReportLinks.AnyAsync(x =>
                x.ArtifactType == "TestChangeRequest" && x.ArtifactId == reviewId
                && x.ProblemReportId == fixture.ProblemReportId));
            Assert.False(await db.ReviewCycles.AnyAsync(x => x.TestChangeReviewId == reviewId));
            Assert.False(await db.UserNotifications.AnyAsync(x =>
                x.ArtifactId == reviewId && x.Type == "TestChangeRequestApprovalRequested"));
            Assert.False(await db.ElectronicSignatures.AnyAsync(x => x.ArtifactId == reviewId));
            Assert.Equal(TestChangeReviewState.Open,
                (await db.TestChangeReviews.SingleAsync(x => x.Id == reviewId)).State);
        }
    }

    [Fact]
    public async Task A_true_link_versus_submit_collision_when_the_submit_commits_first()
    {
        using var factory = new AeroLinkApiFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var reviewId = await PrepareSubmittableNoChangePackageAsync(factory, clientA, fixture.AutoTcrId, fixture.AutoItemId);
        await LoginAsync(clientB, "manual.engineer");
        var version = await CurrentVersionAsync(clientA, fixture.ReleaseId, reviewId);

        using var gate = new SaveRaceGate(factory.ConnectionString);
        try
        {
            var submitTask = clientA.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/submit",
                new { approverId = "manual.reviewer", expectedVersion = version });
            Assert.True(await gate.FirstEnteredAsync(TimeSpan.FromSeconds(30)),
                "The submit request never reached SaveChanges.");
            var linkTask = clientB.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/problem-reports",
                new { problemReportIds = new[] { fixture.ProblemReportId }, expectedVersion = version });
            Assert.True(await gate.SecondEnteredAsync(TimeSpan.FromSeconds(30)),
                "The link request never reached SaveChanges.");

            gate.ReleaseFirst();
            using var submit = await submitTask;
            Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());

            gate.ReleaseSecond();
            using var link = await linkTask;
            var linkBody = await link.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Conflict, link.StatusCode);
            Assert.Contains("stale_version", linkBody);
        }
        finally
        {
            gate.Dispose();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.Empty(await db.ProblemReportLinks.Where(x =>
                x.ArtifactType == "TestChangeRequest" && x.ArtifactId == reviewId).ToListAsync());
            Assert.True(await db.ReviewCycles.AnyAsync(x => x.TestChangeReviewId == reviewId));
            Assert.Equal(TestChangeReviewState.InReview,
                (await db.TestChangeReviews.SingleAsync(x => x.Id == reviewId)).State);
        }
    }

    [Fact]
    public async Task A_true_impact_decision_change_versus_submit_collision_when_the_decision_commits_first()
    {
        using var factory = new AeroLinkApiFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var reviewId = await PrepareSubmittableNoChangePackageAsync(factory, clientA, fixture.AutoTcrId, fixture.AutoItemId);
        await LoginAsync(clientB, "manual.engineer");
        var version = await CurrentVersionAsync(clientA, fixture.ReleaseId, reviewId);

        using var gate = new SaveRaceGate(factory.ConnectionString);
        try
        {
            var reopenTask = clientA.PostAsJsonAsync($"/api/verification-impact/{fixture.AutoItemId}/reopen",
                new { rationale = "Rework the decision before submission." });
            Assert.True(await gate.FirstEnteredAsync(TimeSpan.FromSeconds(30)),
                "The reopen request never reached SaveChanges.");
            var submitTask = clientB.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/submit",
                new { approverId = "manual.reviewer", expectedVersion = version });
            Assert.True(await gate.SecondEnteredAsync(TimeSpan.FromSeconds(30)),
                "The submit request never reached SaveChanges.");

            gate.ReleaseFirst();
            using var reopened = await reopenTask;
            Assert.True(reopened.IsSuccessStatusCode, await reopened.Content.ReadAsStringAsync());

            gate.ReleaseSecond();
            using var submit = await submitTask;
            var submitBody = await submit.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Conflict, submit.StatusCode);
            Assert.Contains("stale_version", submitBody);
        }
        finally
        {
            gate.Dispose();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.False(await db.ReviewCycles.AnyAsync(x => x.TestChangeReviewId == reviewId));
            Assert.Equal(VerificationImpactState.Open,
                (await db.VerificationImpactItems.SingleAsync(x => x.Id == fixture.AutoItemId)).State);
            Assert.Equal(TestChangeReviewState.Open,
                (await db.TestChangeReviews.SingleAsync(x => x.Id == reviewId)).State);
            Assert.False(await db.ElectronicSignatures.AnyAsync(x => x.ArtifactId == reviewId));
        }
    }

    [Fact]
    public async Task Impact_decisions_are_frozen_while_in_review_or_approved()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var reviewId = await PrepareSubmittableNoChangePackageAsync(factory, client, fixture.AutoTcrId, fixture.AutoItemId);
        var version = await CurrentVersionAsync(client, fixture.ReleaseId, reviewId);
        using var submitted = await client.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/submit",
            new { approverId = "manual.reviewer", expectedVersion = version });
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());

        using var reopenInReview = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.AutoItemId}/reopen",
            new { rationale = "Too late." });
        Assert.Equal(HttpStatusCode.Conflict, reopenInReview.StatusCode);
        Assert.Contains("only while the test change request is Open", await reopenInReview.Content.ReadAsStringAsync());

        await LoginAsync(client, "manual.reviewer");
        using var approved = await client.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/approve",
            new { rationale = "Approved.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact test change request." });
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
        await LoginAsync(client, "manual.engineer");
        using var reopenApproved = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.AutoItemId}/reopen",
            new { rationale = "Even later." });
        Assert.Equal(HttpStatusCode.Conflict, reopenApproved.StatusCode);
    }

    [Fact]
    public async Task Returning_to_open_permits_impact_rework_and_resubmission_hashes_differently()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var reviewId = await PrepareSubmittableNoChangePackageAsync(factory, client, fixture.AutoTcrId, fixture.AutoItemId);
        var version = await CurrentVersionAsync(client, fixture.ReleaseId, reviewId);
        using var submitted = await client.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/submit",
            new { approverId = "manual.reviewer", expectedVersion = version });
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());

        string firstHash;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            firstHash = (await db.ReviewCycles.SingleAsync(x => x.TestChangeReviewId == reviewId)).SnapshotHash;
        }

        await LoginAsync(client, "manual.reviewer");
        using var returned = await client.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/return",
            new { rationale = "Rework the decision." });
        Assert.True(returned.IsSuccessStatusCode, await returned.Content.ReadAsStringAsync());

        await LoginAsync(client, "manual.engineer");
        using var reopened = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.AutoItemId}/reopen",
            new { rationale = "Correct the decision after return." });
        Assert.True(reopened.IsSuccessStatusCode, await reopened.Content.ReadAsStringAsync());
        using var resolved = await client.PostAsJsonAsync($"/api/verification-impact/{fixture.AutoItemId}/resolve",
            new { outcome = "NoTestRequired", rationale = "Corrected decision after rework." });
        Assert.True(resolved.IsSuccessStatusCode, await resolved.Content.ReadAsStringAsync());

        var versionAfter = await CurrentVersionAsync(client, fixture.ReleaseId, reviewId);
        using var resubmitted = await client.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/submit",
            new { approverId = "manual.reviewer", expectedVersion = versionAfter });
        Assert.True(resubmitted.IsSuccessStatusCode, await resubmitted.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var cycles = await db.ReviewCycles.Where(x => x.TestChangeReviewId == reviewId)
                .OrderBy(x => x.Sequence).ToListAsync();
            Assert.Equal(2, cycles.Count);
            Assert.Equal(firstHash, cycles[0].SnapshotHash);
            Assert.NotEqual(firstHash, cycles[1].SnapshotHash);
        }
    }

    [Fact]
    public async Task Concurrent_revise_requests_do_not_create_competing_successors()
    {
        using var factory = new AeroLinkApiFactory();
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        var reviewId = await PrepareSubmittableNoChangePackageAsync(factory, clientA, fixture.AutoTcrId, fixture.AutoItemId);
        var version = await CurrentVersionAsync(clientA, fixture.ReleaseId, reviewId);
        using var submitted = await clientA.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/submit",
            new { approverId = "manual.reviewer", expectedVersion = version });
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());
        await LoginAsync(clientA, "manual.reviewer");
        using var approved = await clientA.PostAsJsonAsync($"/api/test-change-reviews/{reviewId}/approve",
            new { rationale = "Approved.", password = AeroLinkApiFactory.MemberPassword, meaning = "I approve this exact test change request." });
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());

        await LoginAsync(clientA, "manual.engineer");
        await LoginAsync(clientB, "manual.engineer");
        var first = clientA.PostAsync($"/api/test-change-reviews/{reviewId}/revise", null);
        var second = clientB.PostAsync($"/api/test-change-reviews/{reviewId}/revise", null);
        var results = await Task.WhenAll(first, second);
        var statuses = results.Select(x => x.StatusCode).OrderBy(x => (int)x).ToArray();
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.True(statuses.Contains(HttpStatusCode.Conflict) || statuses.Contains(HttpStatusCode.BadRequest),
            string.Join(",", statuses));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var successors = await db.TestChangeReviews.CountAsync(x =>
                x.ChangeRequestId == fixture.AutoRaisedChangeId
                && x.Discipline == TestChangeReviewDiscipline.System && x.Revision == 1);
            Assert.Equal(1, successors);
        }
    }
}
