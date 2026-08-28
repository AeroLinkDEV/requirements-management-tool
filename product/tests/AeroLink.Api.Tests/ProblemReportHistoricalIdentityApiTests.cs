using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Person identity on Problem Reports, for accounts a real deployment creates.
///
/// Every account here is created by the test rather than seeded, and none of them appears in the client's
/// <c>PeopleRegistry.ts</c> demo map. That is deliberate and is the point of the whole file: #776 was
/// invisible precisely because the demo accounts resolve client-side, so a suite built on seeded people would
/// pass while every customer account still rendered a login handle.
///
/// The two behaviours being separated are:
///
///   * an immutable audit event keeps the name captured when it happened, whatever the directory says later;
///   * a live assignment follows the directory, because "who owns this now" is a question about now.
/// </summary>
public sealed class ProblemReportHistoricalIdentityApiTests
{
    // Not in PeopleRegistry.ts. Asserted in NonSeededAccountsAreNotInTheDemoRegistry below so that adding
    // them there later — the fix this issue explicitly rules out — breaks this suite loudly.
    private const string EngineerHandle = "dynamic.quality.01";
    private const string EngineerName = "Jordan Lambert";
    private const string RenamedEngineerName = "Jordan Tremblay";
    private const string OwnerHandle = "dynamic.owner.02";
    private const string OwnerName = "Priya Raman";
    // Deliberately a different person from the owner: if the two live fields were ever wired to each other's
    // handle, identical fixtures would hide it.
    private const string ReporterHandle = "dynamic.reporter.03";
    private const string ReporterName = "Sam Okafor";

    [Fact]
    public async Task A_non_seeded_account_is_named_in_the_audit_trail_and_keeps_its_handle()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _) = await SeedAsync(factory);
        var reportId = await RaiseReportAsync(factory, projectId);
        await SignInAsync(client, EngineerHandle);

        await TransitionAsync(client, reportId, "ready-for-sccb");

        var detail = await DetailAsync(client, reportId);
        var latest = Revisions(detail).First();

        // The name is present for an account no demo registry knows about...
        Assert.Equal(EngineerName, latest.GetProperty("actorDisplayName").GetString());
        // ...and the login handle is still there beside it, which is what an auditor reconciles against the
        // identity provider. Replacing the handle would trade one unusable answer for another.
        Assert.Equal(EngineerHandle, latest.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task A_directory_rename_does_not_rewrite_what_a_past_event_says()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _) = await SeedAsync(factory);
        var reportId = await RaiseReportAsync(factory, projectId);
        await SignInAsync(client, EngineerHandle);
        await TransitionAsync(client, reportId, "ready-for-sccb");

        var before = Revisions(await DetailAsync(client, reportId)).First();
        Assert.Equal(EngineerName, before.GetProperty("actorDisplayName").GetString());

        // The person changes their name in the directory. Nothing about the event that already happened
        // changed, so nothing the event says may change either.
        await RenameAsync(factory, EngineerHandle, RenamedEngineerName);

        var after = Revisions(await DetailAsync(client, reportId)).First();
        Assert.Equal(EngineerName, after.GetProperty("actorDisplayName").GetString());
        Assert.NotEqual(RenamedEngineerName, after.GetProperty("actorDisplayName").GetString());
        Assert.Equal(EngineerHandle, after.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task A_live_assignment_does_follow_the_directory()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _) = await SeedAsync(factory);
        var reportId = await RaiseReportAsync(factory, projectId);
        await SignInAsync(client, EngineerHandle);

        var before = await DetailAsync(client, reportId);
        Assert.Equal(OwnerName, before.GetProperty("responsibleEngineerDisplayName").GetString());
        // The reporter is a different person, so a swap between the two fields would show up here.
        Assert.Equal(ReporterName, before.GetProperty("reportedByDisplayName").GetString());
        Assert.Equal(ReporterHandle, before.GetProperty("reportedBy").GetString());

        await RenameAsync(factory, OwnerHandle, "Priya Raman-Osei");

        // The counterpart to the test above: this field answers "who holds this now", so the current answer
        // is the correct one. Current and historical identity are meant to behave differently.
        var after = await DetailAsync(client, reportId);
        Assert.Equal("Priya Raman-Osei", after.GetProperty("responsibleEngineerDisplayName").GetString());
        Assert.Equal(OwnerHandle, after.GetProperty("responsibleEngineerId").GetString());
    }

    [Fact]
    public async Task An_event_recorded_before_names_were_captured_still_reads_as_its_handle()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _) = await SeedAsync(factory);
        var reportId = await RaiseReportAsync(factory, projectId);

        // Exactly the shape of every row already in a deployed database: an actor handle and no captured
        // name. The account exists and has a display name today, which is the trap — the honest answer is
        // still the handle, because nobody recorded what the person was called at the time.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var report = await db.ProblemReports.SingleAsync(x => x.Id == reportId);
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
                "LegacyImported", EngineerHandle, report.CanonicalHash(),
                ProblemReportControlledEditingAdapter.EvidenceSnapshot(report),
                DateTimeOffset.UtcNow.AddYears(-2)));
            await db.SaveChangesAsync();
        }

        await SignInAsync(client, EngineerHandle);
        var legacy = Revisions(await DetailAsync(client, reportId))
            .Single(x => x.GetProperty("eventType").GetString() == "LegacyImported");

        Assert.Equal(EngineerHandle, legacy.GetProperty("actor").GetString());
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("actorDisplayName").ValueKind);
    }

    [Fact]
    public async Task Naming_the_actors_does_not_scale_with_the_length_of_the_history()
    {
        var commands = new ProblemReportPagingCommandInterceptor();
        using var factory = new AeroLinkApiFactory(commandInterceptor: commands);
        using var client = factory.CreateClient();
        var (projectId, _) = await SeedAsync(factory);
        var shortReport = await RaiseReportAsync(factory, projectId, "PR-77601");
        var longReport = await RaiseReportAsync(factory, projectId, "PR-77602");
        await AddHistoryAsync(factory, shortReport, 2);
        await AddHistoryAsync(factory, longReport, 60);
        await SignInAsync(client, EngineerHandle);

        commands.Clear();
        var shortDetail = await DetailAsync(client, shortReport);
        Assert.Equal(2, Revisions(shortDetail).Length);
        var shortLookups = AccountQueries(commands);

        commands.Clear();
        var longDetail = await DetailAsync(client, longReport);
        Assert.Equal(60, Revisions(longDetail).Length);
        var longLookups = AccountQueries(commands);

        // Asserted first, so this test cannot pass by the projection simply not running: with the change
        // reverted there is no directory query at all, both counts collapse to the constant authentication
        // cost, and a bare equality would still hold. Naming has to actually have happened.
        Assert.Equal(OwnerName, longDetail.GetProperty("responsibleEngineerDisplayName").GetString());
        Assert.All(Revisions(longDetail),
            revision => Assert.Equal(EngineerName, revision.GetProperty("actorDisplayName").GetString()));

        // Thirty times the history, and not one extra identity query: #777 was raised about the per-row shape.
        Assert.Equal(shortLookups, longLookups);
    }

    [Fact]
    public async Task A_page_of_the_register_names_its_people_in_one_lookup()
    {
        var commands = new ProblemReportPagingCommandInterceptor();
        using var factory = new AeroLinkApiFactory(commandInterceptor: commands);
        using var client = factory.CreateClient();
        var (projectId, _) = await SeedAsync(factory);
        for (var index = 0; index < 12; index++)
            await RaiseReportAsync(factory, projectId, $"PR-778{index:D2}");
        await SignInAsync(client, EngineerHandle);

        commands.Clear();
        var oneRow = await client.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports?projectId={projectId}&page=1&pageSize=1");
        var oneRowLookups = AccountQueries(commands);

        commands.Clear();
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/problem-reports?projectId={projectId}&page=1&pageSize=12");
        var fullPageLookups = AccountQueries(commands);

        var rows = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(12, rows.Length);
        // Every row is named, and both live fields are named independently of each other.
        Assert.All(rows, row =>
        {
            Assert.Equal(OwnerName, row.GetProperty("responsibleEngineerDisplayName").GetString());
            Assert.Equal(ReporterName, row.GetProperty("reportedByDisplayName").GetString());
        });
        Assert.Single(oneRow.GetProperty("items").EnumerateArray());

        // Twelve rows cost what one row costs. This is the exact shape #777 was about, on the surface that
        // renders a person per row.
        Assert.Equal(oneRowLookups, fullPageLookups);
    }

    private static int AccountQueries(ProblemReportPagingCommandInterceptor commands) =>
        commands.Commands.Count(text => text.Contains("user_accounts", StringComparison.OrdinalIgnoreCase));

    private static async Task AddHistoryAsync(AeroLinkApiFactory factory, Guid reportId, int events)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var report = await db.ProblemReports.SingleAsync(x => x.Id == reportId);
        var snapshot = ProblemReportControlledEditingAdapter.EvidenceSnapshot(report);
        var hash = report.CanonicalHash();
        for (var index = 0; index < events; index++)
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision,
                "LegacyImported", EngineerHandle, hash, snapshot,
                DateTimeOffset.UtcNow.AddMinutes(-index), actorDisplayName: EngineerName));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_closure_invalidation_event_names_its_actor_even_without_a_session_supplied_name()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var (projectId, _) = await SeedAsync(factory);
        var reportId = await RaiseReportAsync(factory, projectId);

        // Written the way the controlled check-in engine and the link service write it: a bare handle, no
        // session-supplied name. The service must still capture one rather than leaving the event nameless.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var report = await db.ProblemReports.SingleAsync(x => x.Id == reportId);
            db.ProblemReportClosureCandidates.Add(new ProblemReportClosureCandidate(report.Id, report.Revision,
                1, 1, report.Version, ProblemReportControlledEditingAdapter.EvidenceSnapshot(report),
                new string('a', 64), Guid.NewGuid(), "{}", new string('b', 64), "{}", new string('c', 64),
                new string('d', 64), EngineerHandle, DateTimeOffset.UtcNow,
                ProblemReportEvidenceContract.SchemaVersion));
            await db.SaveChangesAsync();

            await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(report, EngineerHandle,
                "DetailsCheckedIn", DateTimeOffset.UtcNow, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await SignInAsync(client, EngineerHandle);
        var invalidation = Revisions(await DetailAsync(client, reportId))
            .Single(x => x.GetProperty("eventType").GetString() == "ClosureVerificationInvalidatedByChange");

        Assert.Equal(EngineerName, invalidation.GetProperty("actorDisplayName").GetString());
        Assert.Equal(EngineerHandle, invalidation.GetProperty("actor").GetString());
    }

    [Fact]
    public void Non_seeded_accounts_are_not_in_the_demo_registry()
    {
        // Guards the fix the issue rules out: making the demo registry bigger would make these tests pass
        // while changing nothing for a real deployment.
        var registry = File.ReadAllText(RegistryPath());
        Assert.DoesNotContain(EngineerHandle, registry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OwnerHandle, registry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ReporterHandle, registry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(EngineerName, registry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OwnerName, registry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ReporterName, registry, StringComparison.OrdinalIgnoreCase);
    }

    private static string RegistryPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "product", "client")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "product", "client", "src", "PeopleRegistry.ts");
    }

    private static JsonElement[] Revisions(JsonElement detail) =>
        detail.GetProperty("revisions").EnumerateArray().ToArray();

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid reportId)
    {
        using var response = await client.GetAsync($"/api/problem-reports/{reportId}");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task TransitionAsync(HttpClient client, Guid reportId, string route)
    {
        using var response = await client.PostAsJsonAsync($"/api/problem-reports/{reportId}/{route}",
            new { rationale = "Controlled transition for identity coverage." });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task RenameAsync(AeroLinkApiFactory factory, string userName, string displayName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var account = await db.UserAccounts.SingleAsync(x => x.UserName == userName);
        account.RefreshDirectoryProfile(displayName, account.Email);
        await db.SaveChangesAsync();
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private static async Task<Guid> RaiseReportAsync(AeroLinkApiFactory factory, Guid projectId, string number = "PR-77600")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var report = new ProblemReport(projectId, number, "Display adapter drops annunciation",
            "Observed on a warm restart of the display unit.", "", ReporterHandle, DateTimeOffset.UtcNow,
            responsibleEngineerId: OwnerHandle, category: ProblemReportCategory.CodeFunctional);
        db.ProblemReports.Add(report);
        await db.SaveChangesAsync();
        return report.Id;
    }

    private static async Task<(Guid ProjectId, Guid ProgramId)> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Deployment identity", $"DID{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);

        var engineer = new UserAccount(EngineerHandle, EngineerName, $"{EngineerHandle}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var owner = new UserAccount(OwnerHandle, OwnerName, $"{OwnerHandle}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        var reporter = new UserAccount(ReporterHandle, ReporterName, $"{ReporterHandle}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(engineer, owner, reporter);
        db.AddRange(
            new ProgramMembership(engineer.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(engineer.Id, program.Id, ProgramRole.SoftwareQualityAnalyst, "test.setup", now),
            new ProgramMembership(owner.Id, program.Id, ProgramRole.Engineer, "test.setup", now),
            new ProgramMembership(reporter.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return (project.Id, program.Id);
    }
}
