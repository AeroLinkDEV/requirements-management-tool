using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The verification workspace rendered every procedure it was given — 440 cards on the software side — with
/// no search, no filter and no page. Finding one meant scrolling, and the client received far more than it
/// could show.
///
/// These drive the endpoint rather than the projection, because a bounded page that is bounded only in the
/// browser is not paging.
/// </summary>
public sealed class ProcedureBrowsingApiTests
{
    private const string Member = "procedure.browser";

    private static async Task<Guid> SeedAsync(AeroLinkApiFactory factory, int count = 40)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Browsing Program", "BRW");
        var project = new ProjectRecord(program.Id, "Software", "Browsing Software");
        db.AddRange(program, project);

        for (var index = 1; index <= count; index++)
        {
            var owner = index % 2 == 0 ? "test.author" : "other.author";
            var procedure = new TestProcedure(project.Id, $"SYSTP-{index:D8}", $"Verify behaviour {index:D3}", owner, now,
                TestProcedureLevel.System);
            // Every third procedure is approved, so state filtering has something to separate. Approved at
            // construction, as materialisation writes it — there is no separate signature on a revision.
            var revision = new TestProcedureRevision(procedure.Id, 1, "Objective", "Preconditions", "Steps", "Expected",
                index % 3 == 0 ? TestProcedureState.Approved : TestProcedureState.Draft, owner, now);
            db.AddRange(procedure, revision);

            // One procedure carries a Fail then a later Pass, so "latest outcome" and "any outcome" differ.
            if (index == 6)
            {
                db.Add(new TestExecution(project.Id, revision.Id, null, null, TestOutcome.Fail, "test.engineer", "Rig",
                    "Earlier run failed.", "evidence/a.json", now.AddHours(-2), now.AddHours(-2)));
                db.Add(new TestExecution(project.Id, revision.Id, null, null, TestOutcome.Pass, "test.engineer", "Rig",
                    "Later run passed.", "evidence/b.json", now, now));
            }
        }

        var account = new UserAccount(Member, Member, $"{Member}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task SignInAsync(HttpClient client, string user = Member)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, Guid projectId, string query = "")
    {
        using var response = await client.GetAsync($"/api/test-procedures?projectId={projectId}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Numbers(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("displayNumber").GetString()!)];

    [Fact]
    public async Task Paging_is_bounded_reports_the_total_and_walks_every_record_exactly_once()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);
        await SignInAsync(client);

        var first = await PageAsync(client, projectId, "&page=1&pageSize=10");
        Assert.Equal(40, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, first.GetProperty("totalPages").GetInt32());
        Assert.Equal(10, Numbers(first).Length);

        // Walking the pages must yield each record once — a boundary that depends on tie order does not.
        var walked = new List<string>();
        for (var page = 1; page <= 4; page++) walked.AddRange(Numbers(await PageAsync(client, projectId, $"&page={page}&pageSize=10")));
        Assert.Equal(40, walked.Count);
        Assert.Equal(40, walked.Distinct().Count());

        // Repeating a page returns the same rows in the same order.
        Assert.Equal(Numbers(first), Numbers(await PageAsync(client, projectId, "&page=1&pageSize=10")));

        // The page size is clamped rather than trusted.
        Assert.True(Numbers(await PageAsync(client, projectId, "&page=1&pageSize=100000")).Length <= 200);
    }

    [Fact]
    public async Task Search_state_owner_and_latest_outcome_each_narrow_the_set_and_the_total()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);
        await SignInAsync(client);

        var byNumber = await PageAsync(client, projectId, "&search=SYSTP-00000007");
        Assert.Equal(1, byNumber.GetProperty("totalCount").GetInt32());
        Assert.Equal("SYSTP-00000007.01", Numbers(byNumber).Single());
        var legacy = byNumber.GetProperty("items")[0];
        Assert.StartsWith("Legacy procedure SYSTP-00000007.01", legacy.GetProperty("title").GetString());
        Assert.False(legacy.GetProperty("titleIsExact").GetBoolean());
        Assert.True(legacy.GetProperty("titleIsLegacy").GetBoolean());
        Assert.Contains("exact historical title was not recorded", legacy.GetProperty("titleNote").GetString());

        // The catalog title is mutable current metadata, not an immutable snapshot belonging to this legacy
        // revision. Searching it must not silently attribute today's value to historical controlled work.
        var byMutableLegacyTitle = await PageAsync(client, projectId, "&search=behaviour%20012");
        Assert.Equal(0, byMutableLegacyTitle.GetProperty("totalCount").GetInt32());
        Assert.Empty(Numbers(byMutableLegacyTitle));

        var approved = await PageAsync(client, projectId, "&state=Approved&pageSize=200");
        Assert.Equal(13, approved.GetProperty("totalCount").GetInt32());
        Assert.All(approved.GetProperty("items").EnumerateArray(), x => Assert.Equal("Approved", x.GetProperty("state").GetString()));

        var owned = await PageAsync(client, projectId, "&owner=other.author&pageSize=200");
        Assert.Equal(20, owned.GetProperty("totalCount").GetInt32());

        // The one procedure with two runs failed first and passed last, so it must answer to Pass and not Fail.
        var passed = await PageAsync(client, projectId, "&outcome=Pass&pageSize=200");
        Assert.Equal("SYSTP-00000006.01", Numbers(passed).Single());
        Assert.Equal(0, (await PageAsync(client, projectId, "&outcome=Fail&pageSize=200")).GetProperty("totalCount").GetInt32());

        // Filters compose, and the total reflects the filtered population rather than the whole project.
        var composed = await PageAsync(client, projectId, "&state=Approved&owner=test.author&pageSize=200");
        Assert.True(composed.GetProperty("totalCount").GetInt32() < approved.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Sort_order_is_explicit_and_stable_across_pages()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);
        await SignInAsync(client);

        foreach (var sort in new[] { "identifier", "title", "owner", "level" })
        {
            var walked = new List<string>();
            for (var page = 1; page <= 4; page++)
                walked.AddRange(Numbers(await PageAsync(client, projectId, $"&sort={sort}&page={page}&pageSize=10")));
            Assert.Equal(40, walked.Distinct().Count());
        }
    }

    [Fact]
    public async Task Software_HLR_and_LLR_scopes_return_only_their_own_procedures()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var hlr = new TestProcedure(projectId, "HLRTP-000001", "Verify HLR", "test.author", now, TestProcedureLevel.HighLevel);
            var llr = new TestProcedure(projectId, "LLRTP-000001", "Verify LLR", "test.author", now, TestProcedureLevel.LowLevel);
            db.AddRange(hlr, llr,
                new TestProcedureRevision(hlr.Id, 0, "HLR", "Ready", "Run", "Pass", TestProcedureState.Draft, "test.author", now),
                new TestProcedureRevision(llr.Id, 0, "LLR", "Ready", "Run", "Pass", TestProcedureState.Draft, "test.author", now));
            await db.SaveChangesAsync();
        }
        await SignInAsync(client);

        Assert.Equal(["HLRTP-000001.00"], Numbers(await PageAsync(client, projectId, "&scope=HighLevelSoftware")));
        Assert.Equal(["LLRTP-000001.00"], Numbers(await PageAsync(client, projectId, "&scope=LowLevelSoftware")));
    }

    [Fact]
    public async Task Search_accepts_a_full_display_number_with_revision()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory, 1);
        await SignInAsync(client);

        Assert.Equal(["SYSTP-00000001.01"], Numbers(await PageAsync(client, projectId, "&search=SYSTP-00000001.01")));
    }

    [Fact]
    public async Task The_procedure_list_is_not_readable_without_access_to_the_project()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await SeedAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            db.Add(new UserAccount("procedure.outsider", "procedure.outsider", "outsider@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        await SignInAsync(client, "procedure.outsider");
        using var response = await client.GetAsync($"/api/test-procedures?projectId={projectId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
