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

    private static object SourceRecord(int number, bool inImportedBaseline = true, object[]? history = null) => new
    {
        sourceModule = "FMS_System_Requirements",
        sourceObjectKey = number.ToString(),
        sourceIdentifier = $"SYS-{number:00000}",
        inImportedBaseline,
        history
    };

    private static Task<HttpResponseMessage> RecordSourceRecordsAsync(HttpClient client, Guid id, params object[] records) =>
        client.PostAsJsonAsync($"/api/baseline-imports/{id}/source-records", new { records });

    /// <summary>Walks an import to Reconciled, which now means it has really been told what the extract held.</summary>
    private static async Task WalkToReconciledAsync(HttpClient client, Guid id, string mapping = "{}")
    {
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/baseline-imports/{id}/analysis", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping",
            new { mappingJson = mapping })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, id, SourceRecord(1234), SourceRecord(1235))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/reconciliation",
            new { reconciliationJson = """{"objectsIn":2,"requirementsOut":2}""" })).StatusCode);
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

        await WalkToReconciledAsync(client, id, """{"modules":{"FMS_System_Requirements":"System"}}""");

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

        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = "{}" });
        // Mapped, but the import has not been told what the extract held. Reconciling "every object is
        // accounted for" against no objects is vacuously true, and would produce an empty build asserting a
        // program was brought in from elsewhere.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/baseline-imports/{id}/reconciliation", new { reconciliationJson = """{"objectsIn":0}""" })).StatusCode);

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

        await WalkToReconciledAsync(client, id, """{"v":1}""");

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
        await WalkToReconciledAsync(client, id);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" })).StatusCode);

        // An accepted import is immutable: its baseline exists.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/baseline-imports/{id}/abandon", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await RecordSourceRecordsAsync(client, id, SourceRecord(1236))).StatusCode);

        var second = await StartAsync(client, projectId);
        await WalkToReconciledAsync(client, second);
        using var collision = await client.PostAsJsonAsync($"/api/baseline-imports/{second}/accept", new { version = "1.0" });
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
    }

    [Fact]
    public async Task An_import_records_what_the_extract_held_and_a_re_extract_is_a_delta()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "DLT");
        var id = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{id}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = "{}" });

        using var first = await RecordSourceRecordsAsync(client, id,
            SourceRecord(1233, inImportedBaseline: false), SourceRecord(1234), SourceRecord(1235));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, firstBody.GetProperty("recorded").GetInt32());
        Assert.Equal(0, firstBody.GetProperty("seenAgain").GetInt32());

        // A later extract of the same program. The same objects, not a second set of them — that is what the
        // source's own stable key is for, and it holds even when the identifier text was edited in between.
        using var again = await RecordSourceRecordsAsync(client, id, SourceRecord(1234), SourceRecord(1235), SourceRecord(1236));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, againBody.GetProperty("recorded").GetInt32());
        Assert.Equal(2, againBody.GetProperty("seenAgain").GetInt32());
        // Everything in the payload was accounted for, whether new here or already known — which is what the
        // Reconcile gate needs. Counting rows this import created would have said one.
        Assert.Equal(3, againBody.GetProperty("accountedFor").GetInt32());

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{id}");
        Assert.Equal(3, detail.GetProperty("sourceRecordCount").GetInt32());
        // Four identities exist: three in the baseline and one the source retired before it.
        Assert.Equal(4, detail.GetProperty("sourceIdentityCount").GetInt32());
        Assert.Equal(3, detail.GetProperty("sourceRecords").GetProperty("inImportedBaseline").GetInt32());
        Assert.Equal(1, detail.GetProperty("sourceRecords").GetProperty("historyOnly").GetInt32());
    }

    [Fact]
    public async Task Two_objects_claiming_the_same_source_identity_are_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "DUP");
        var id = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{id}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = "{}" });

        // Refused outright rather than reported at Reconcile as a gap somebody could accept: there is no
        // mapping decision that makes two objects with one key safe, because a later extract cannot tell
        // them apart, and the delta rule would silently merge them.
        using var refused = await RecordSourceRecordsAsync(client, id, SourceRecord(1234), SourceRecord(1234));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("cannot be told apart", await refused.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // Nothing was half-written: a refused payload leaves the import exactly as it was.
        Assert.Empty(await db.SourceIdentities.AsNoTracking().Where(x => x.BaselineImportId == id).ToListAsync());
    }

    [Fact]
    public async Task Source_history_is_recorded_as_reported_and_never_becomes_a_revision()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "HST");
        var id = await StartAsync(client, projectId);
        await client.PostAsync($"/api/baseline-imports/{id}/analysis", null);
        await client.PostAsJsonAsync($"/api/baseline-imports/{id}/mapping", new { mappingJson = "{}" });

        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, id, SourceRecord(1234, history:
        [
            new { sourceBaselineName = "V0.8", statement = "", changedBy = "", changedAt = (DateTimeOffset?)null, sourceChangeReference = "" },
            new
            {
                sourceBaselineName = "V0.9",
                statement = "The FMS shall annunciate a navigation source disagreement.",
                changedBy = "a.okafor",
                changedAt = (DateTimeOffset?)new DateTimeOffset(2025, 1, 22, 0, 0, 0, TimeSpan.Zero),
                sourceChangeReference = "DOORS CR-1402"
            }
        ]))).StatusCode);

        var records = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{id}/source-records");
        var record = Assert.Single(records.EnumerateArray());
        var history = record.GetProperty("sourceHistory").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        // A source that recorded no author, date or statement is described as it was found. Nothing
        // downstream reasons over any of it, which is exactly what makes recording it honestly safe.
        Assert.Equal("V0.8", history[0].GetProperty("sourceBaselineName").GetString());
        Assert.Equal("", history[0].GetProperty("changedBy").GetString());
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("changedAt").ValueKind);
        Assert.Equal("DOORS CR-1402", history[1].GetProperty("sourceChangeReference").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        // History is held against the source identity, never as requirement revisions. A revision here binds
        // a change request and a materialized baseline; importing V0.8 as one would mean fabricating both.
        Assert.Equal(2, await db.SourceHistoryEntries.AsNoTracking().CountAsync(x => x.BaselineImportId == id));
        Assert.Empty(await db.RequirementRevisions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Recording_more_of_the_extract_makes_the_import_unacceptable_again()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "AGN");
        var id = await StartAsync(client, projectId);
        await WalkToReconciledAsync(client, id);

        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, id, SourceRecord(9001))).StatusCode);

        // The reconciliation described a different set of objects. Accepting against it would be accepting
        // counts that no longer say what this import would do.
        var afterMore = await client.GetFromJsonAsync<JsonElement>($"/api/baseline-imports/{id}");
        Assert.Equal("Mapped", afterMore.GetProperty("state").GetString());
        Assert.Equal("", afterMore.GetProperty("reconciliationJson").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/baseline-imports/{id}/accept", new { version = "1.0" })).StatusCode);
    }

    [Fact]
    public async Task A_released_build_in_the_workspace_does_not_refuse_an_import()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory, "RLS");
        Guid releasedId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var released = new SoftwareRelease(projectId, "1.5", isReleased: false);
            released.MarkReleased(DateTimeOffset.UtcNow);
            db.Releases.Add(released);
            await db.SaveChangesAsync();
            releasedId = released.Id;
        }

        client.DefaultRequestHeaders.Add("X-AeroLink-Build-Context", releasedId.ToString());

        // The released-build refusal stops a released build being edited. An import creates a new build from
        // a source that is already released, so refusing it would answer a question nobody asked — and it
        // would, because "/api/baseline" is loose enough to catch "/api/baselines" and so catches
        // "/api/baseline-imports" with it.
        var id = await StartAsync(client, projectId);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/baseline-imports/{id}/analysis", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RecordSourceRecordsAsync(client, id, SourceRecord(1234))).StatusCode);

        // The refusal still holds for everything it was written for.
        using var refused = await client.PostAsJsonAsync($"/api/baselines?projectId={projectId}&releaseId={releasedId}",
            new { name = "Attempted baseline" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("released_build_read_only", await refused.Content.ReadAsStringAsync());
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
        Assert.Equal(HttpStatusCode.Forbidden, (await RecordSourceRecordsAsync(client, id, SourceRecord(1234))).StatusCode);
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
