using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// Controlled numbers used to be derived by loading every identifier of a prefix and adding one to the
/// maximum, which means two allocations that overlap read the same maximum and choose the same number. These
/// cover the allocator directly rather than through a create endpoint, because the interesting moment is
/// between handing a number out and committing the record that uses it — the window the old design left open.
/// </summary>
public sealed class IdentifierAllocationTests
{
    [Fact]
    public async Task Authoring_context_previews_do_not_claim_controlled_numbers()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory);

        // A software preview names its level: HLRCR and LLRCR are numbered apart, so the server cannot
        // answer for "Software" alone.
        async Task<JsonElement> Preview(string type, string? level = null) =>
            await client.GetFromJsonAsync<JsonElement>(
                $"/api/authoring/context?projectId={projectId}&type={type}{(level is null ? "" : $"&softwareLevel={level}")}");

        var firstSystem = await Preview("System");
        var secondSystem = await Preview("System");
        // The legacy preview helper ignores an optional software level on a System request. The aggregate
        // constructor remains strict; this assertion protects the read-only preview compatibility seam.
        var legacySystemWithSoftwareLevel = await Preview("System", "HighLevel");
        var firstSoftware = await Preview("Software", "HighLevel");
        var secondSoftware = await Preview("Software", "HighLevel");

        Assert.Equal("SRCR-00001", firstSystem.GetProperty("changeRequestNumber").GetString());
        Assert.Equal(firstSystem.GetRawText(), secondSystem.GetRawText());
        Assert.Equal("SRCR-00001", legacySystemWithSoftwareLevel.GetProperty("changeRequestNumber").GetString());
        Assert.Equal("HLRCR-00001", firstSoftware.GetProperty("changeRequestNumber").GetString());
        Assert.Equal(firstSoftware.GetRawText(), secondSoftware.GetRawText());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var previewedScopes = new[] { "SRCR", "HLRCR", "SYSR", "HLR", "LLR" };
        Assert.Empty(await db.IdentifierSequences.AsNoTracking().Where(x => previewedScopes.Contains(x.Scope)).ToListAsync());

        Assert.Equal("SRCR-00001", await IdentifierAllocator.NextChangeRequestAsync(db, ChangeRequestType.System, null, default));
        Assert.Equal("SYSR-000001", await IdentifierAllocator.NextRequirementAsync(db, "SYSR", default));
    }

    [Fact]
    public async Task An_existing_database_starts_numbering_past_what_it_already_recorded()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory);

        // A problem report created through the API seeds the PR sequence; deleting the sequence row leaves the
        // database exactly as an upgrade from before this table would find it.
        using var created = await client.PostAsJsonAsync("/api/problem-reports", new { projectId, title = "Unexpected reset", problem = "The unit resets during a route update.", analysis = "", classification = "Verification failure", severity = "High", priority = "Urgent", origin = "Test execution", affectedConfiguration = "Build 1.6.0" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        db.IdentifierSequences.RemoveRange(await db.IdentifierSequences.ToListAsync());
        await db.SaveChangesAsync();

        Assert.Equal("PR-00002", await IdentifierAllocator.NextProblemReportAsync(db, default));
    }

    [Fact]
    public async Task Two_uploads_of_one_logical_file_leave_exactly_one_active_version()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory);
        using var report = await client.PostAsJsonAsync("/api/problem-reports", new { projectId, title = "Attachment host", problem = "The unit resets during a route update.", analysis = "", classification = "Verification failure", severity = "High", priority = "Urgent", origin = "Test execution", affectedConfiguration = "Build 1.6.0" });
        Assert.Equal(HttpStatusCode.Created, report.StatusCode);
        var artifactId = (await report.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var logicalId = Guid.NewGuid();

        using var one = await UploadAsync(client, projectId, artifactId, logicalId, "first");
        Assert.Equal(HttpStatusCode.Created, one.StatusCode);

        // The state two overlapping uploads leave behind, written directly because two requests cannot be held
        // mid-flight against each other here — SQLite serializes them, so the second always sees the first's row
        // and the interesting case never arises. Version 2 is Active alongside version 1: each upload superseded
        // only the row it had read, and neither read the other.
        using (var seedScope = factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var existing = await seedDb.ControlledAttachments.AsNoTracking().SingleAsync(x => x.LogicalId == logicalId);
            // Claimed rather than assigned, because that is what the upload it stands in for would have done.
            var version = await IdentifierAllocator.ClaimAsync(seedDb, "ATTACHMENT-" + logicalId.ToString("N"),
                () => Task.FromResult(1), default);
            seedDb.ControlledAttachments.Add(new ControlledAttachment(projectId, "ProblemReport", artifactId, null, logicalId, version,
                "second", "Concurrent upload", "second.txt", "text/plain", 8, existing.Sha256, existing.StorageKey + "-2",
                null, "admin", DateTimeOffset.UtcNow));
            await seedDb.SaveChangesAsync();
        }

        // The next upload is what reconciles it: everything but the highest version ends up superseded, so the
        // logical file has one current version again rather than two.
        using var three = await UploadAsync(client, projectId, artifactId, logicalId, "third");
        Assert.Equal(HttpStatusCode.Created, three.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var versions = await db.ControlledAttachments.AsNoTracking().Where(x => x.LogicalId == logicalId).ToListAsync();
        Assert.Equal(new[] { 1, 2, 3 }, versions.Select(x => x.Version).Order());
        var active = versions.Where(x => x.State == ControlledAttachmentState.Active).ToList();
        Assert.Single(active);
        Assert.Equal(3, active[0].Version);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid projectId, Guid artifactId, Guid logicalId, string label)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(projectId.ToString()), "projectId" },
            { new StringContent("ProblemReport"), "artifactType" },
            { new StringContent(artifactId.ToString()), "artifactId" },
            { new StringContent(logicalId.ToString()), "logicalId" },
            { new StringContent(label), "label" },
            { new StringContent("Concurrent upload"), "description" },
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes($"contents of {label}")) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") } }, "file", $"{label}.txt" },
        };
        return client.PostAsync("/api/enterprise-hardening/attachments", content);
    }

    [Fact]
    public async Task Concurrent_allocations_of_one_prefix_all_receive_distinct_numbers()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await BootstrapAndLoginAsync(client);
        var projectId = await SeedProjectAsync(factory);

        var posts = Enumerable.Range(0, 8).Select(index => client.PostAsJsonAsync("/api/problem-reports",
            new { projectId, title = $"Concurrent finding {index}", problem = "The unit resets during a route update.", analysis = "", classification = "Verification failure", severity = "High", priority = "Urgent", origin = "Test execution", affectedConfiguration = "Build 1.6.0" })).ToList();
        var responses = await Task.WhenAll(posts);

        foreach (var response in responses) Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var numbers = await db.ProblemReports.AsNoTracking().Select(x => x.ReportNumber).ToListAsync();
        Assert.Equal(8, numbers.Count);
        Assert.Equal(8, numbers.Distinct().Count());
        foreach (var response in responses) response.Dispose();
    }

    private static async Task<Guid> SeedProjectAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Identifier Program", $"ID{Guid.NewGuid():N}"[..12]); var project = new ProjectRecord(program.Id, "Flight Management Product", "Flight Management System");
        db.AddRange(program, project); await db.SaveChangesAsync(); return project.Id;
    }

    private static async Task BootstrapAndLoginAsync(HttpClient client)
    {
        using var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap") { Content = JsonContent.Create(new { displayName = "AeroLink Administrator", email = "admin@example.test", password = AeroLinkApiFactory.AdministratorPassword }) };
        bootstrap.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret); Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(bootstrap)).StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword }); Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
