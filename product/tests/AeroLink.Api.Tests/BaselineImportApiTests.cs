using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Walking a program in from another tool, through the API.
///
/// The gates exist so that nothing is committed blind, and the acceptance exists so that a named person owns
/// the assertion. What must never happen is the thing these cover most closely: an imported baseline that
/// cannot be told apart from one this product built.
/// </summary>
public sealed class BaselineImportApiTests
{
    private const string Digest = "9f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38";

    private static async Task<Guid> SeedProjectAsync(AeroLinkApiFactory factory, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord($"{prefix} Program", $"{prefix}{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        db.AddRange(program, project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static object StartBody(Guid projectId, string[]? carries = null) => new
    {
        projectId,
        sourceSystem = "IBM Rational DOORS",
        sourceSystemVersion = "9.6.1.13",
        sourceBaselineName = "FMS Sys Req v4.2",
        sourceBaselineDate = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero),
        extractFileName = "FMS_SYSTEM_REQUIREMENTS_2026-07-14.reqifz",
        extractSha256 = Digest,
        extractSizeBytes = 43_842_112L,
        carries = carries ?? ["Requirements"],
        extractedBy = "m.chen",
        extractedAt = new DateTimeOffset(2026, 7, 14, 9, 12, 0, TimeSpan.Zero)
    };

    private static async Task<Guid> StartAsync(HttpClient client, Guid projectId)
    {
        using var created = await client.PostAsJsonAsync("/api/baseline-imports", StartBody(projectId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task An_import_walks_its_five_gates_and_becomes_a_released_build()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "IMP");

        var id = await StartAsync(client, projectId);

        var started = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{id}");
        Assert.Equal("Draft", started.GetProperty("state").GetString());
        Assert.Equal(Digest, started.GetProperty("extractSha256").GetString());
        // The assertion is stated by the record itself rather than left for a reader to infer.
        Assert.Contains("were not", started.GetProperty("doesNotAssert").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/baseline-imports/{id}/analysis", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping",
            new { mappingJson = """{"modules":{"FMS_System_Requirements":"System"}}""" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/reconciliation",
            new { reconciliationJson = """{"objectsIn":5412,"requirementsOut":5180}""" })).StatusCode);

        using var accepted = await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var detail = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Accepted", detail.GetProperty("state").GetString());
        Assert.Equal("admin", detail.GetProperty("acceptedBy").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var releaseId = detail.GetProperty("releaseId").GetGuid();
        var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == releaseId);

        // Released on arrival: readiness gates evaluate a build before release, and this one is already past
        // that. Its prior decisions belong to the source's own release, not to anything done here.
        Assert.Equal("1.0", release.Version);
        Assert.True(release.IsReleased);
        Assert.NotNull(release.ReleasedAt);

        // And the build is externally sourced because an accepted import points at it — the fact is derived
        // from the provenance rather than duplicated into a flag that could drift away from it.
        Assert.True(await db.BaselineImports.AsNoTracking()
            .AnyAsync(x => x.ReleaseId == releaseId && x.State == BaselineImportState.Accepted));
    }

    [Fact]
    public async Task No_gate_can_be_skipped()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "GAT");
        var id = await StartAsync(client, projectId);

        // Straight to accept, from Draft.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/baseline-imports/{id}/reconciliation", new { reconciliationJson = "{}" })).StatusCode);

        await client.PostAsync($"/api/baseline-imports/{id}/analysis", null);
        // Analysed, but nothing has been mapped, so there is nothing to reconcile.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/baseline-imports/{id}/reconciliation", new { reconciliationJson = "{}" })).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // Nothing partial was written along the way.
        Assert.Empty(await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync());
    }

    [Fact]
    public async Task Changing_the_mapping_makes_the_import_unacceptable_again()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "MAP");
        var id = await StartAsync(client, projectId);

        await client.PostAsync($"/api/baseline-imports/{id}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = """{"v":1}""" });
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/reconciliation", new { reconciliationJson = """{"in":10}""" });

        // Remapping discards the reconciliation, because those counts described the old mapping. Accepting
        // against them would be accepting something other than what the import would now do.
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = """{"v":2}""" });

        var afterRemap = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{id}");
        Assert.Equal("Mapped", afterRemap.GetProperty("state").GetString());
        Assert.Equal("", afterRemap.GetProperty("reconciliationJson").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" })).StatusCode);
    }

    [Fact]
    public async Task Provenance_that_could_not_be_checked_later_is_refused_at_the_door()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "PRV");

        using var noHash = await client.PostAsJsonAsync("/api/baseline-imports", new
        {
            projectId, sourceSystem = "DOORS", sourceSystemVersion = "9.6", sourceBaselineName = "v4.2",
            sourceBaselineDate = DateTimeOffset.UtcNow, extractFileName = "x.reqifz",
            extractSha256 = "not-a-digest", extractSizeBytes = 10L, carries = new[] { "Requirements" },
            extractedBy = "m.chen", extractedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(HttpStatusCode.BadRequest, noHash.StatusCode);

        using var noKind = await client.PostAsJsonAsync("/api/baseline-imports", StartBody(projectId, []));
        Assert.Equal(HttpStatusCode.BadRequest, noKind.StatusCode);

        using var unknownKind = await client.PostAsJsonAsync("/api/baseline-imports", StartBody(projectId, ["Drawings"]));
        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
    }

    [Fact]
    public async Task An_import_declares_carrying_requirements_and_test_procedures_separately()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "KND");

        using var created = await client.PostAsJsonAsync("/api/baseline-imports",
            StartBody(projectId, ["Requirements", "TestProcedures"]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var carries = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("carries").GetString();
        Assert.Contains("Requirements", carries);
        Assert.Contains("TestProcedures", carries);
    }

    [Fact]
    public async Task Accepting_twice_or_onto_an_existing_build_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "TWC");
        var id = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{id}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = "{}" });
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/reconciliation", new { reconciliationJson = "{}" });
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" })).StatusCode);

        // An accepted import is immutable: its baseline exists.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/baseline-imports/{id}/abandon", null)).StatusCode);

        var second = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{second}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{second}/mapping", new { mappingJson = "{}" });
        await client.PostAsJsonAsync($"/api/baseline-imports/{second}/reconciliation", new { reconciliationJson = "{}" });
        using var collision = await client.PostAsJsonAsync($"/api/baseline-imports/{second}/accept", new { version = "1.0" });
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
    }

    [Fact]
    public async Task Porting_a_program_in_takes_Program_authority_not_engineering_authority()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        Guid projectId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Import Authority Program", "IAU");
            var project = new ProjectRecord(program.Id, "Flight Software", "Import Authority Software");
            db.AddRange(program, project);
            foreach (var (userName, role) in new[]
                     {
                         ("import.engineer", ProgramRole.Engineer),
                         ("import.cm", ProgramRole.ConfigurationManager),
                     })
            {
                var account = new UserAccount(userName, userName, $"{userName}@example.test",
                    IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
                db.Add(account);
                db.Add(new ProgramMembership(account.Id, program.Id, role, "test.setup", now));
            }
            await db.SaveChangesAsync();
            projectId = project.Id;
        }

        // An engineer has every right to work inside this Program. Declaring that a whole baseline arrived
        // from somewhere else, already released, is not that kind of act — it is Program setup, so it takes
        // the authority that establishes a Project.
        await SignInAsync(client, "import.engineer");
        using var refused = await client.PostAsJsonAsync("/api/baseline-imports", StartBody(projectId));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        await SignInAsync(client, "import.cm");
        using var allowed = await client.PostAsJsonAsync("/api/baseline-imports", StartBody(projectId));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        var id = (await allowed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The same holds for every later gate, not only for starting one.
        await SignInAsync(client, "import.engineer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/baseline-imports/{id}/analysis", null)).StatusCode);
        // Reading is not the same as asserting: anyone in the Program can see where a requirement came from.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/baseline-imports/{id}")).StatusCode);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task A_source_identifier_retired_before_the_imported_baseline_still_answers()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "SRC");
        var id = await StartAsync(client, projectId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var current = new SourceIdentity(projectId, id, "IBM Rational DOORS",
                "FMS_System_Requirements", "1234", "SYS-01234", now);
            var retired = SourceIdentity.FromHistoryOnly(projectId, id, "IBM Rational DOORS",
                "FMS_System_Requirements", "1233", "SYS-01233", now);
            db.AddRange(current, retired);
            db.Add(new SourceHistoryEntry(projectId, current.Id, id, "V0.9",
                "The FMS shall annunciate a navigation source disagreement.", "a.okafor",
                new DateTimeOffset(2025, 1, 22, 0, 0, 0, TimeSpan.Zero), "DOORS CR-1402"));
            await db.SaveChangesAsync();
        }

        // Somebody holding a drawing that cites a retired identifier gets an answer, not an empty result.
        var retiredHit = await client.GetFromJsonAsync<JsonElement>(
            $"/api/source-identities?projectId={projectId}&search=SYS-01233");
        var row = Assert.Single(retiredHit.EnumerateArray());
        Assert.Equal("SYS-01233", row.GetProperty("sourceIdentifier").GetString());
        // It joins nothing: history is narrative, not nodes, so it can never be a dangling reference.
        Assert.False(row.GetProperty("inImportedBaseline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("requirementRevisionId").ValueKind);

        var currentHit = await client.GetFromJsonAsync<JsonElement>(
            $"/api/source-identities?projectId={projectId}&search=SYS-01234");
        var live = Assert.Single(currentHit.EnumerateArray());
        Assert.True(live.GetProperty("inImportedBaseline").GetBoolean());
        // Source history is reported as found, attributed to the source system, and claimed by nobody here.
        var history = Assert.Single(live.GetProperty("sourceHistory").EnumerateArray());
        Assert.Equal("V0.9", history.GetProperty("sourceBaselineName").GetString());
        Assert.Equal("DOORS CR-1402", history.GetProperty("sourceChangeReference").GetString());
    }
}
