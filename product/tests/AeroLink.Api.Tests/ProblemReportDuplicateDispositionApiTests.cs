using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class ProblemReportDuplicateDispositionApiTests
{
    [Fact]
    public async Task Invalid_duplicate_targets_leave_the_controlled_record_unchanged()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, otherProjectId) = await SeedProjectsAsync(factory);
        var source = await CreateAsync(client, projectId, "Source anomaly");
        var crossProject = await CreateAsync(client, otherProjectId, "Other Project anomaly");

        foreach (var (targetId, expectedCode) in new[]
        {
            (Guid.NewGuid(), "pr_duplicate_target_not_in_project"),
            (crossProject.Id, "pr_duplicate_target_not_in_project"),
            (source.Id, "pr_duplicate_self_reference"),
        })
        {
            using var response = await client.PostAsJsonAsync($"/api/problem-reports/{source.Id}/disposition", new
            {
                expectedVersion = source.Version,
                disposition = "Duplicate",
                rationale = "The report is represented by the selected canonical record.",
                duplicateOfId = targetId,
            });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(expectedCode,
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        using (var missingTarget = await DispositionAsync(client, source.Id, source.Version, "Duplicate", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingTarget.StatusCode);
            Assert.Equal("pr_duplicate_target_required",
                (await missingTarget.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        var sameProject = await CreateAsync(client, projectId, "Valid target not used by another disposition");
        using (var unrelatedDisposition = await DispositionAsync(client, source.Id, source.Version,
                   "CannotReproduce", sameProject.Id))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unrelatedDisposition.StatusCode);
            Assert.Equal("pr_duplicate_target_unexpected",
                (await unrelatedDisposition.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{source.Id}");
        Assert.Equal("Draft", detail.GetProperty("state").GetString());
        Assert.Equal(source.Version, detail.GetProperty("version").GetInt64());
        Assert.Empty(detail.GetProperty("links").EnumerateArray());
        Assert.Single(detail.GetProperty("revisions").EnumerateArray());
    }

    [Fact]
    public async Task Valid_duplicate_is_atomic_resolvable_and_retained_as_history_after_reopen()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, _) = await SeedProjectsAsync(factory);
        var source = await CreateAsync(client, projectId, "Repeated navigation anomaly");
        var canonical = await CreateAsync(client, projectId, "Canonical navigation anomaly");

        using var accepted = await DispositionAsync(client, source.Id, source.Version, "Duplicate", canonical.Id);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        var duplicateVersion = acceptedBody.GetProperty("version").GetInt64();
        Assert.Equal("Duplicate", acceptedBody.GetProperty("state").GetString());

        var duplicate = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{source.Id}");
        var link = Assert.Single(duplicate.GetProperty("links").EnumerateArray());
        Assert.Equal(ProblemReportRelationshipPolicy.DuplicateOf, link.GetProperty("relationship").GetString());
        Assert.Equal(canonical.Id, link.GetProperty("artifactId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(link.GetProperty("identifier").GetString()));
        var diagnostic = duplicate.GetProperty("duplicateDiagnostic");
        Assert.Equal(ProblemReportDuplicateDispositionPolicy.PolicyName, diagnostic.GetProperty("policy").GetString());
        Assert.Equal("Valid", diagnostic.GetProperty("status").GetString());
        Assert.Equal(canonical.Id, diagnostic.GetProperty("canonicalTargetId").GetGuid());
        Assert.Equal(2, duplicate.GetProperty("revisions").GetArrayLength());

        using var reopened = await client.PostAsJsonAsync($"/api/problem-reports/{source.Id}/reopen", new
        {
            expectedVersion = duplicateVersion,
            rationale = "New observations require another controlled investigation.",
        });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var reopenedVersion = (await reopened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();
        var reopenedDetail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{source.Id}");
        Assert.Equal("Open", reopenedDetail.GetProperty("state").GetString());
        Assert.Single(reopenedDetail.GetProperty("links").EnumerateArray());
        Assert.Equal("Historical", reopenedDetail.GetProperty("duplicateDiagnostic").GetProperty("status").GetString());

        var competing = await CreateAsync(client, projectId, "Competing canonical anomaly");
        using var refused = await DispositionAsync(client, source.Id, reopenedVersion, "Duplicate", competing.Id);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("pr_duplicate_history_already_exists",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var unchanged = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{source.Id}");
        Assert.Equal(reopenedVersion, unchanged.GetProperty("version").GetInt64());
        Assert.Single(unchanged.GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task Canonical_root_policy_refuses_direct_and_transitive_cycles_and_arbitrary_chains()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, _) = await SeedProjectsAsync(factory);
        var a = await CreateAsync(client, projectId, "Anomaly A");
        var b = await CreateAsync(client, projectId, "Anomaly B");
        var c = await CreateAsync(client, projectId, "Anomaly C");

        using (var accepted = await DispositionAsync(client, a.Id, a.Version, "Duplicate", b.Id))
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using (var directCycle = await DispositionAsync(client, b.Id, b.Version, "Duplicate", a.Id))
        {
            Assert.Equal(HttpStatusCode.BadRequest, directCycle.StatusCode);
            Assert.Equal("pr_duplicate_cycle",
                (await directCycle.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using (var chain = await DispositionAsync(client, b.Id, b.Version, "Duplicate", c.Id))
        {
            Assert.Equal(HttpStatusCode.BadRequest, chain.StatusCode);
            Assert.Equal("pr_duplicate_source_is_canonical",
                (await chain.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // Model a retained A -> B -> C chain from before the canonical-root policy. It stays readable but C -> A
        // must still be refused because walking the legacy path reaches the source.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var storedB = await db.ProblemReports.SingleAsync(item => item.Id == b.Id);
            storedB.ApplyDisposition("admin", ProblemReportDisposition.Duplicate,
                "Legacy chain fixture.", c.Id, DateTimeOffset.UtcNow);
            db.ProblemReportLinks.Add(new ProblemReportLink(b.Id, "ProblemReport", c.Id,
                ProblemReportRelationshipPolicy.DuplicateOf, "legacy.fixture", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var transitiveCycle = await DispositionAsync(client, c.Id, c.Version, "Duplicate", a.Id);
        Assert.Equal(HttpStatusCode.BadRequest, transitiveCycle.StatusCode);
        Assert.Equal("pr_duplicate_cycle",
            (await transitiveCycle.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var unchangedC = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{c.Id}");
        Assert.Equal("Draft", unchangedC.GetProperty("state").GetString());
        Assert.Equal(c.Version, unchangedC.GetProperty("version").GetInt64());
        Assert.Empty(unchangedC.GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task Legacy_dangling_and_cyclic_relationships_are_diagnosed_without_being_rewritten()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, _) = await SeedProjectsAsync(factory);
        Guid danglingId, cycleAId, cycleBId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var dangling = new ProblemReport(projectId, "PR-09001", "Dangling legacy duplicate",
                "Historical target is missing.", "", "admin", now);
            var cycleA = new ProblemReport(projectId, "PR-09002", "Legacy cycle A",
                "Historical cycle.", "", "admin", now);
            var cycleB = new ProblemReport(projectId, "PR-09003", "Legacy cycle B",
                "Historical cycle.", "", "admin", now);
            var missing = Guid.NewGuid();
            dangling.ApplyDisposition("admin", ProblemReportDisposition.Duplicate, "Legacy fixture.", missing, now);
            cycleA.ApplyDisposition("admin", ProblemReportDisposition.Duplicate, "Legacy fixture.", cycleB.Id, now);
            cycleB.ApplyDisposition("admin", ProblemReportDisposition.Duplicate, "Legacy fixture.", cycleA.Id, now);
            db.AddRange(dangling, cycleA, cycleB,
                new ProblemReportLink(dangling.Id, "ProblemReport", missing,
                    ProblemReportRelationshipPolicy.DuplicateOf, "legacy.fixture", now),
                new ProblemReportLink(cycleA.Id, "ProblemReport", cycleB.Id,
                    ProblemReportRelationshipPolicy.DuplicateOf, "legacy.fixture", now),
                new ProblemReportLink(cycleB.Id, "ProblemReport", cycleA.Id,
                    ProblemReportRelationshipPolicy.DuplicateOf, "legacy.fixture", now));
            await db.SaveChangesAsync();
            (danglingId, cycleAId, cycleBId) = (dangling.Id, cycleA.Id, cycleB.Id);
        }

        var danglingDetail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{danglingId}");
        Assert.Equal("DanglingTarget", danglingDetail.GetProperty("duplicateDiagnostic").GetProperty("status").GetString());
        var cycleDetail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{cycleAId}");
        Assert.Equal("Cycle", cycleDetail.GetProperty("duplicateDiagnostic").GetProperty("status").GetString());

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(3, await verificationDb.ProblemReportLinks.CountAsync(link =>
            link.ProblemReportId == danglingId || link.ProblemReportId == cycleAId || link.ProblemReportId == cycleBId));
    }

    [Fact]
    public async Task Stale_and_simultaneous_dispositions_never_persist_competing_targets()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, _) = await SeedProjectsAsync(factory);
        var source = await CreateAsync(client, projectId, "Concurrent source anomaly");
        var firstTarget = await CreateAsync(client, projectId, "First canonical target");
        var secondTarget = await CreateAsync(client, projectId, "Second canonical target");

        using (var stale = await DispositionAsync(client, source.Id, source.Version + 1, "Duplicate", firstTarget.Id))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var responses = await Task.WhenAll(
            DispositionAsync(client, source.Id, source.Version, "Duplicate", firstTarget.Id),
            DispositionAsync(client, source.Id, source.Version, "Duplicate", secondTarget.Id));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{source.Id}");
        Assert.Equal("Duplicate", detail.GetProperty("state").GetString());
        Assert.Single(detail.GetProperty("links").EnumerateArray());
        Assert.Equal(2, detail.GetProperty("revisions").GetArrayLength());
        Assert.Equal("Valid", detail.GetProperty("duplicateDiagnostic").GetProperty("status").GetString());
    }

    private static async Task<(Guid ProjectId, Guid OtherProjectId)> SeedProjectsAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Duplicate disposition Program", $"DP{Guid.NewGuid():N}"[..12]);
        var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        var otherProgram = new ProgramRecord("Other duplicate Program", $"DX{Guid.NewGuid():N}"[..12]);
        var otherProject = new ProjectRecord(otherProgram.Id, "Other Product", "Other System");
        db.AddRange(program, project, otherProgram, otherProject);
        await db.SaveChangesAsync();
        return (project.Id, otherProject.Id);
    }

    private static async Task<(Guid Id, long Version)> CreateAsync(HttpClient client, Guid projectId, string title)
    {
        using var response = await client.PostAsJsonAsync("/api/problem-reports", new
        {
            projectId,
            title,
            problem = "A controlled anomaly requires an exact canonical duplicate decision.",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("version").GetInt64());
    }

    private static Task<HttpResponseMessage> DispositionAsync(HttpClient client, Guid reportId, long version,
        string disposition, Guid? duplicateOfId) =>
        client.PostAsJsonAsync($"/api/problem-reports/{reportId}/disposition", new
        {
            expectedVersion = version,
            disposition,
            rationale = "The selected report is the same controlled anomaly.",
            duplicateOfId,
        });
}
