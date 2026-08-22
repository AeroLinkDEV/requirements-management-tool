using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
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
public sealed class ProcedureBrowsingApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public ProcedureBrowsingApiTests(SharedApiHost host)
    {
        _host = host;
    }

    private static async Task<(Guid ProjectId, string MemberName)> SeedAsync(AeroLinkApiFactory factory, int count = 40)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        // Unique per test: user accounts and Program codes are globally unique-constrained, so a shared
        // host/database requires per-test identities.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var member = $"procedure.browser.{tag}";
        var program = new ProgramRecord($"Browsing Program {tag}", $"BRW{tag}");
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

        var account = new UserAccount(member, member, $"{member}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
        await db.SaveChangesAsync();
        return (project.Id, member);
    }

    private static async Task SignInAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = user, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, Guid projectId, string query = "",
        string route = "/api/test-procedures")
    {
        using var response = await client.GetAsync($"{route}?projectId={projectId}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Numbers(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("displayNumber").GetString()!)];

    [Fact]
    public async Task Paging_is_bounded_reports_the_total_and_walks_every_record_exactly_once()
    {
        using var client = _host.CreateClient();
        var seeded = await SeedAsync(_host.Factory);
        var projectId = seeded.ProjectId;
        await SignInAsync(client, seeded.MemberName);

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
        using var client = _host.CreateClient();
        var seeded = await SeedAsync(_host.Factory);
        var projectId = seeded.ProjectId;
        await SignInAsync(client, seeded.MemberName);

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
        using var client = _host.CreateClient();
        var seeded = await SeedAsync(_host.Factory);
        var projectId = seeded.ProjectId;
        await SignInAsync(client, seeded.MemberName);

        foreach (var sort in new[] { "identifier", "title", "owner", "level" })
        {
            var walked = new List<string>();
            for (var page = 1; page <= 4; page++)
                walked.AddRange(Numbers(await PageAsync(client, projectId, $"&sort={sort}&page={page}&pageSize=10")));
            Assert.Equal(40, walked.Distinct().Count());
        }
    }

    [Fact]
    public async Task Software_scopes_return_Cases_and_System_remains_a_Procedure()
    {
        using var client = _host.CreateClient();
        var seeded = await SeedAsync(_host.Factory, 0);
        var projectId = seeded.ProjectId;
        Guid highCaseId;
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var hlr = new TestProcedure(projectId, "HLRTC-000001", "Verify HLR", "test.author", now, TestProcedureLevel.HighLevel);
            highCaseId = hlr.Id;
            var llr = new TestProcedure(projectId, "LLRTC-000001", "Verify LLR", "test.author", now, TestProcedureLevel.LowLevel);
            var system = new TestProcedure(projectId, "SYSTP-00000001", "Verify System", "test.author", now, TestProcedureLevel.System);
            db.AddRange(new SoftwareRelease(projectId, $"722-{Guid.NewGuid():N}", true), hlr, llr, system,
                new TestProcedureRevision(hlr.Id, 0, "HLR", "Ready", "Run", "Pass", TestProcedureState.Draft, "test.author", now),
                new TestProcedureRevision(llr.Id, 0, "LLR", "Ready", "Run", "Pass", TestProcedureState.Draft, "test.author", now),
                new TestProcedureRevision(system.Id, 0, "System", "Ready", "Run", "Pass", TestProcedureState.Draft, "test.author", now));
            await db.SaveChangesAsync();
        }
        await SignInAsync(client, seeded.MemberName);

        var highCases = await PageAsync(client, projectId, "&scope=HighLevelSoftware", "/api/test-cases");
        var lowCases = await PageAsync(client, projectId, "&scope=LowLevelSoftware", "/api/test-cases");
        Assert.Equal(["HLRTC-000001.00"], Numbers(highCases));
        Assert.Equal(["LLRTC-000001.00"], Numbers(lowCases));
        Assert.All(highCases.GetProperty("items").EnumerateArray(), item => Assert.Equal("Case", item.GetProperty("artifactKind").GetString()));
        Assert.All(lowCases.GetProperty("items").EnumerateArray(), item => Assert.Equal("Case", item.GetProperty("artifactKind").GetString()));

        // The old path remains a compatibility alias for clients that cannot migrate in lockstep; it must
        // return the same Case-shaped software records rather than reclassifying them as Procedures.
        var legacySoftwareAlias = await PageAsync(client, projectId, "&scope=HighLevelSoftware", "/api/test-procedures");
        Assert.Equal(Numbers(highCases), Numbers(legacySoftwareAlias));
        Assert.All(legacySoftwareAlias.GetProperty("items").EnumerateArray(), item => Assert.Equal("Case", item.GetProperty("artifactKind").GetString()));

        // System remains the one genuine Procedure surface and continues to use its established route.
        var systemProcedures = await PageAsync(client, projectId, "&scope=System", "/api/test-procedures");
        Assert.Equal(["SYSTP-00000001.00"], Numbers(systemProcedures));
        Assert.All(systemProcedures.GetProperty("items").EnumerateArray(), item => Assert.Equal("Procedure", item.GetProperty("artifactKind").GetString()));

        // Notification and other external links use the current Case identity. The legacy Procedure resolver
        // remains available separately, but a current software link must land on the canonical Case route.
        using var direct = _host.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsync(direct, seeded.MemberName);
        using var redirect = await direct.GetAsync($"/open/case/{highCaseId}");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Contains($"/software-verification/cases?caseId={highCaseId}",
            redirect.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_accepts_a_full_display_number_with_revision()
    {
        using var client = _host.CreateClient();
        var seeded = await SeedAsync(_host.Factory, 1);
        var projectId = seeded.ProjectId;
        await SignInAsync(client, seeded.MemberName);

        Assert.Equal(["SYSTP-00000001.01"], Numbers(await PageAsync(client, projectId, "&search=SYSTP-00000001.01")));
    }

    [Fact]
    public async Task Signature_api_projects_migration_supersession_without_mutating_original_evidence()
    {
        using var client = _host.CreateClient();
        var seeded = await SeedAsync(_host.Factory, 0);
        await SignInAsync(client, seeded.MemberName);
        Guid signatureId;
        Guid artifactId;
        const string oldHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string newHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var project = await db.Projects.SingleAsync(x => x.Id == seeded.ProjectId);
            var account = await db.UserAccounts.SingleAsync(x => x.UserName == seeded.MemberName);
            var now = DateTimeOffset.UtcNow;
            var signature = new ElectronicSignature(account.Id, account.UserName, account.DisplayName, project.ProgramId,
                "LegacyHistoricalEvidence", Guid.NewGuid(), "HLRTP-000321.00", "Approve", "Historical evidence", oldHash,
                "127.0.0.1", now);
            signatureId = signature.Id;
            artifactId = signature.ArtifactId;
            var target = $"ElectronicSignature:{signature.Id}";
            db.AddRange(signature,
                new SecurityAuditEvent("VerificationIdentityMigration.SignatureSuperseded", "aerolink-migration", target,
                    "Superseded", JsonSerializer.Serialize(new
                    {
                        migration = "VerificationIdentityMigration.SoftwareCases.v1",
                        oldArtifactIdentity = signature.ArtifactRevision,
                        oldSignatureHash = oldHash,
                        newArtifactIdentity = "HLRTC-000321.00",
                        newContentHash = (string?)null,
                        reason = "Controlled output was regenerated; a new human signature is required."
                    }), "", now),
                new SecurityAuditEvent("VerificationIdentityMigration.SignatureSupersessionCompleted", "aerolink-migration", target,
                    "Succeeded", JsonSerializer.Serialize(new
                    {
                        migration = "VerificationIdentityMigration.SoftwareCases.v1",
                        oldArtifactIdentity = signature.ArtifactRevision,
                        oldSignatureHash = oldHash,
                        newArtifactIdentity = "HLRTC-000321.00",
                        newContentHash = newHash,
                        reason = "Stored controlled bytes were regenerated by the governed migration authority."
                    }), "", now.AddSeconds(1)));
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync($"/api/signatures?artifactId={artifactId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal("Superseded", row.GetProperty("signatureStatus").GetString());
        Assert.True(row.GetProperty("isSuperseded").GetBoolean());
        var provenance = row.GetProperty("supersession");
        Assert.Equal("VerificationIdentityMigration.SoftwareCases.v1", provenance.GetProperty("migration").GetString());
        Assert.Equal(oldHash, provenance.GetProperty("oldSignatureHash").GetString());
        Assert.Equal(newHash, provenance.GetProperty("newContentHash").GetString());
        Assert.Equal("HLRTP-000321.00", row.GetProperty("artifactRevision").GetString());
        Assert.Equal(oldHash, row.GetProperty("contentHash").GetString());
    }

    [Fact]
    public async Task The_procedure_list_is_not_readable_without_access_to_the_project()
    {
        using var client = _host.CreateClient();
        var seeded = await SeedAsync(_host.Factory);
        var projectId = seeded.ProjectId;

        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var outsiderName = $"procedure.outsider.{Guid.NewGuid():N}";
            db.Add(new UserAccount(outsiderName, outsiderName, "outsider@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            await SignInAsync(client, outsiderName);
        }
        using var response = await client.GetAsync($"/api/test-procedures?projectId={projectId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
