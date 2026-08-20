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
    public async Task Legacy_duplicate_decisions_are_read_only_and_leave_the_controlled_record_unchanged()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, otherProjectId) = await SeedProjectsAsync(factory);
        var source = await CreateAsync(client, projectId, "Source anomaly");
        var crossProject = await CreateAsync(client, otherProjectId, "Other Project anomaly");

        foreach (var targetId in new[]
        {
            Guid.NewGuid(),
            crossProject.Id,
            source.Id,
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
            Assert.Equal("pr_legacy_disposition_read_only",
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        using (var missingTarget = await DispositionAsync(client, source.Id, source.Version, "Duplicate", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingTarget.StatusCode);
            Assert.Equal("pr_legacy_disposition_read_only",
                (await missingTarget.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        var sameProject = await CreateAsync(client, projectId, "Valid target not used by another disposition");
        using (var unrelatedDisposition = await DispositionAsync(client, source.Id, source.Version,
                   "CannotReproduce", sameProject.Id))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unrelatedDisposition.StatusCode);
            Assert.Equal("pr_legacy_disposition_read_only",
                (await unrelatedDisposition.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{source.Id}");
        Assert.Equal("Draft", detail.GetProperty("state").GetString());
        Assert.Equal(source.Version, detail.GetProperty("version").GetInt64());
        Assert.Empty(detail.GetProperty("links").EnumerateArray());
        Assert.Single(detail.GetProperty("revisions").EnumerateArray());
    }

    [Fact]
    public async Task New_duplicate_decisions_are_rejected_while_historical_links_remain_readable()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, _) = await SeedProjectsAsync(factory);
        var source = await CreateAsync(client, projectId, "Repeated navigation anomaly");
        var canonical = await CreateAsync(client, projectId, "Canonical navigation anomaly");

        using var refused = await DispositionAsync(client, source.Id, source.Version, "Duplicate", canonical.Id);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("pr_legacy_disposition_read_only",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var unchanged = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{source.Id}");
        Assert.Equal("Draft", unchanged.GetProperty("state").GetString());
        Assert.Equal(source.Version, unchanged.GetProperty("version").GetInt64());
        Assert.Empty(unchanged.GetProperty("links").EnumerateArray());
        Assert.Equal("None", unchanged.GetProperty("duplicateDiagnostic").GetProperty("status").GetString());

        // The historical diagnostic surface remains available even though new duplicate mutations are closed.
        using var reopenAttempt = await client.PostAsJsonAsync($"/api/problem-reports/{source.Id}/reopen", new
        {
            expectedVersion = source.Version,
            rationale = "New observations require another controlled investigation.",
        });
        Assert.Equal(HttpStatusCode.BadRequest, reopenAttempt.StatusCode);
    }

    [Fact]
    public async Task Canonical_duplicate_mutations_are_closed_while_new_records_remain_unchanged()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);
        var (projectId, _) = await SeedProjectsAsync(factory);
        var a = await CreateAsync(client, projectId, "Anomaly A");
        var b = await CreateAsync(client, projectId, "Anomaly B");
        var c = await CreateAsync(client, projectId, "Anomaly C");

        foreach (var (source, target) in new[] { (a, b), (b, a), (b, c), (c, a) })
        {
            using var refused = await DispositionAsync(client, source.Id, source.Version, "Duplicate", target.Id);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("pr_legacy_disposition_read_only",
                (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        foreach (var report in new[] { a, b, c })
        {
            var unchanged = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{report.Id}");
            Assert.Equal("Draft", unchanged.GetProperty("state").GetString());
            Assert.Equal(report.Version, unchanged.GetProperty("version").GetInt64());
            Assert.Empty(unchanged.GetProperty("links").EnumerateArray());
        }
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
    public async Task Stale_and_simultaneous_legacy_duplicate_requests_never_mutate_the_record()
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
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
            foreach (var response in responses)
                Assert.Equal("pr_legacy_disposition_read_only",
                    (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/problem-reports/{source.Id}");
        Assert.Equal("Draft", detail.GetProperty("state").GetString());
        Assert.Empty(detail.GetProperty("links").EnumerateArray());
        Assert.Single(detail.GetProperty("revisions").EnumerateArray());
        Assert.Equal("None", detail.GetProperty("duplicateDiagnostic").GetProperty("status").GetString());
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
