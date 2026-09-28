using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Content;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

// Authoring gate: HTTP authority, outcome/signature binding, retries and atomic publication need a real host.
// The fixture is produced by the Java extractor, with independently asserted destination and source facts.
public sealed class IntegrityImportApiTests
{
    private const string Root = "/api/problem-reports/integrity-import";
    [Fact]
    public async Task A_java_package_is_previewed_signed_once_preserved_and_retrievable_only_by_import_authority()
    {
        using var factory = new AeroLinkApiFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrityImport:AllowFixtures"] = "true" })));
        using var client = configured.CreateClient(); await Bootstrap(client);
        Guid projectId; string engineer = "integrity.engineer"; const string manager = "integrity.cm";
        using (var scope = configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>(); var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Integrity import", "INT"); var project = new ProjectRecord(program.Id, "Destination", "PR");
            var account = new UserAccount(engineer, "Engineer", "engineer@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var cm = new UserAccount(manager, "Configuration Manager", "cm@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(cm, new ProgramMembership(cm.Id, program.Id, ProgramRole.ConfigurationManager, "admin", now));
            db.AddRange(program, project, account, new ProgramMembership(account.Id, program.Id, ProgramRole.SoftwareEngineer, "admin", now));
            await db.SaveChangesAsync(); projectId = project.Id;
        }
        var (bytes, hash) = await IntegrityFixturePackage.ReadAsync();
        var mapping = Mapping(engineer); var operation = Guid.NewGuid();
        MultipartFormDataContent Form(string? previewHash = null, string? password = null, ProblemReportImportMapping? overrideMapping = null) =>
            BuildForm(projectId, bytes, hash, overrideMapping ?? mapping, operation, previewHash, password);
        using var previewResponse = await client.PostAsync("/api/problem-reports/integrity-import/preview", Form());
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<IntegrityImportPreview>(IntegritySourcePackage.Json);
        Assert.NotNull(preview); Assert.Equal(2, preview.Create); Assert.Equal(0, preview.Blocked);
        Assert.Equal("date-only", preview.Rows[1].SourceDate.Meaning);
        Assert.Contains(preview.Rows[0].Outcomes, x => x.Kind == "history" && x.Outcome == "preserved");
        using (var incorrect = await client.PostAsync(Root + "/commit", Form(preview.PreviewHash, "wrong"))) Assert.Equal(HttpStatusCode.Unauthorized, incorrect.StatusCode);
        using (var stale = await client.PostAsync(Root + "/commit", Form(new string('0', 64), AeroLinkApiFactory.AdministratorPassword))) Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var commit = await client.PostAsync("/api/problem-reports/integrity-import/commit", Form(preview.PreviewHash, AeroLinkApiFactory.AdministratorPassword));
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        var receipt = (await commit.Content.ReadFromJsonAsync<IntegrityImportReceipt>(IntegritySourcePackage.Json))!;
        Assert.Equal(2, receipt.Created);
        var imageId = Assert.Single(RichContent.ReferencedAttachments(preview.Rows[0].Problem));
        Assert.StartsWith("data:image/png;base64,", preview.Images![imageId]);
        using (var image = await client.GetAsync($"/api/content/images/{imageId}")) Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        using var retry = await client.PostAsync(Root + "/commit", Form(preview.PreviewHash, AeroLinkApiFactory.AdministratorPassword));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(receipt.BatchId, (await retry.Content.ReadFromJsonAsync<IntegrityImportReceipt>(IntegritySourcePackage.Json))!.BatchId);
        var changed = mapping with { Statuses = new(mapping.Statuses) { ["Active"] = "Draft" } };
        using (var reused = await client.PostAsync(Root + "/commit", Form(preview.PreviewHash, AeroLinkApiFactory.AdministratorPassword, changed))) Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        using (var staleAfterImport = await client.PostAsync(Root + "/commit", BuildForm(projectId, bytes, hash, mapping, Guid.NewGuid(), preview.PreviewHash, AeroLinkApiFactory.AdministratorPassword)))
            Assert.Equal(HttpStatusCode.Conflict, staleAfterImport.StatusCode);
        Guid packageAttachment;
        using (var scope = configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var reports = await db.ProblemReports.OrderBy(x => x.SourceKey).ToListAsync(); Assert.Equal(2, reports.Count);
            Assert.Equal(DateTimeOffset.Parse("2024-03-01T10:34:56.123456Z"), reports[0].SourceCreatedAt);
            Assert.Null(reports[1].SourceCreatedAt); Assert.True(reports[1].ClosedInSource); Assert.Null(reports[1].ClosureApprovedAt);
            Assert.Contains("\"bold\":true", reports[0].ProblemRich);
            var batch = Assert.Single(await db.IntegrityImportBatches.ToListAsync()); packageAttachment = batch.PackageAttachmentId;
            Assert.Equal(preview.PreviewHash, Assert.Single(await db.ElectronicSignatures.Where(x => x.Action == "ImportIntegrityProblemReports").ToListAsync()).ContentHash);
            Assert.Equal(2, await db.IntegrityReportSources.CountAsync());
            Assert.Equal(2, await db.ProblemReportRevisions.CountAsync()); // source history did not become native lifecycle
            Assert.False(await db.ProblemReportLinks.AnyAsync()); // directed source relation did not become a native link
            var original = batch.ManifestHash;
            db.Entry(batch).Property(x => x.ManifestHash).CurrentValue = new string('a', 64);
            await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            db.Entry(batch).Property(x => x.ManifestHash).CurrentValue = original;
        }
        using (var package = await client.GetAsync($"{Root}/batches/{receipt.BatchId}/package")) Assert.Equal(bytes, await package.Content.ReadAsByteArrayAsync());
        var firstItem = IntegritySourcePackage.Read(bytes, hash, true).Items[0].Item;
        using (var member = await client.GetAsync($"{Root}/batches/{receipt.BatchId}/member?path={Uri.EscapeDataString(firstItem.Attachments[0].Path)}"))
        { Assert.Equal(HttpStatusCode.OK, member.StatusCode); Assert.Contains("preserve these bytes exactly", await member.Content.ReadAsStringAsync()); }
        using (var reconciliation = await client.GetAsync($"{Root}/batches/{receipt.BatchId}")) Assert.Equal(HttpStatusCode.OK, reconciliation.StatusCode);
        using (var batches = await client.GetAsync($"{Root}/batches?projectId={projectId}")) Assert.Equal(HttpStatusCode.OK, batches.StatusCode);
        using var memberClient = configured.CreateClient(); await MemberSession.SignInAsync(memberClient, engineer);
        using (var image = await memberClient.GetAsync($"/api/content/images/{imageId}")) Assert.Equal(HttpStatusCode.Forbidden, image.StatusCode);
        using (var forbidden = await memberClient.GetAsync($"{Root}/batches/{receipt.BatchId}/package")) Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var forbidden = await memberClient.GetAsync($"{Root}/batches/{receipt.BatchId}/member?path=items/1001.json")) Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var forbidden = await memberClient.PostAsync(Root + "/preview", Form())) Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var bypass = await memberClient.GetAsync($"/api/enterprise-hardening/attachments/{packageAttachment}/download")) Assert.Equal(HttpStatusCode.NotFound, bypass.StatusCode);
        using var cmClient = configured.CreateClient(); await MemberSession.SignInAsync(cmClient, manager);
        using (var permitted = await cmClient.GetAsync($"{Root}/batches/{receipt.BatchId}/package")) Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
        Guid disabledProject;
        using (var scope = configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var cmId = await db.UserAccounts.Where(x => x.UserName == manager).Select(x => x.Id).SingleAsync();
            db.ProgramMemberships.RemoveRange(await db.ProgramMemberships.Where(x => x.UserId == cmId).ToListAsync());
            var program = new ProgramRecord("No Problem Reports", "NOPR");
            var project = new ProjectRecord(program.Id, "Feature disabled", "NONE");
            db.AddRange(program, project, new ProjectFeatureSet(project.Id, ProjectFeature.TeamWork, "admin", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(); disabledProject = project.Id;
        }
        using (var revoked = await cmClient.GetAsync($"{Root}/batches/{receipt.BatchId}/package")) Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using (var disabled = await client.PostAsync(Root + "/preview", BuildForm(disabledProject, bytes, hash, mapping, Guid.NewGuid())))
            Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
    }
    [Theory]
    [InlineData("object-promoted-1")]
    [InlineData("available-recorded")]
    public async Task Failure_before_commit_rolls_back_reports_signature_ledger_and_blob_references(string phase)
    {
        using var factory = new AeroLinkApiFactory(storageFaultInjector: new OneShotStorageFaultInjector("IntegrityImport", phase));
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrityImport:AllowFixtures"] = "true" })));
        using var client = configured.CreateClient(); await Bootstrap(client);
        var project = await SeedProject(configured.Services);
        var (bytes, hash) = await IntegrityFixturePackage.ReadAsync(); var mapping = Mapping("admin"); var operation = Guid.NewGuid();
        using var previewResponse = await client.PostAsync(Root + "/preview", BuildForm(project, bytes, hash, mapping, operation));
        var preview = (await previewResponse.Content.ReadFromJsonAsync<IntegrityImportPreview>(IntegritySourcePackage.Json))!;
        using var failed = await client.PostAsync(Root + "/commit", BuildForm(project, bytes, hash, mapping, operation, preview.PreviewHash, AeroLinkApiFactory.AdministratorPassword));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using var scope = configured.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.False(await db.ProblemReports.AnyAsync()); Assert.False(await db.IntegrityImportBatches.AnyAsync());
        Assert.False(await db.IntegrityReportSources.AnyAsync());
        Assert.False(await db.ElectronicSignatures.AnyAsync(x => x.Action == "ImportIntegrityProblemReports"));
        Assert.False(await db.ControlledAttachments.AnyAsync(x => x.ProjectId == project));
        Assert.Equal(ManagedDocumentStorageOperationState.RolledBack, (await db.ManagedDocumentStorageOperations.SingleAsync()).State);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<EvidenceFileStore>().EnumerateStagedKeys());
        // Definitive rollback needs a fresh reviewed operation; successful/uncertain commits retain the original identity.
        using var retried = await client.PostAsync(Root + "/commit", BuildForm(project, bytes, hash, mapping, Guid.NewGuid(), preview.PreviewHash, AeroLinkApiFactory.AdministratorPassword));
        Assert.True(retried.IsSuccessStatusCode, await retried.Content.ReadAsStringAsync());
    }
    internal static async Task<Guid> SeedProject(IServiceProvider services)
    {
        using var scope = services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Integrity qualification", "INT");
        var project = new ProjectRecord(program.Id, "Destination", "PR"); db.AddRange(program, project);
        await db.SaveChangesAsync(); return project.Id;
    }
    internal static ProblemReportImportMapping Mapping(string engineer) => new()
    {
        Columns = new() { ["title"] = "Summary", ["problem"] = "Description", ["status"] = "State", ["responsibleEngineer"] = "Owner",
            ["reportedBy"] = "Reporter", ["createdAt"] = "Created", ["category"] = "Category" },
        Statuses = new() { ["Active"] = "Open", ["Closed"] = "ClosedInSource" }, People = new() { ["Source Engineer"] = engineer },
    };
    internal static MultipartFormDataContent BuildForm(Guid project, byte[] bytes, string hash, ProblemReportImportMapping mapping,
        Guid operation, string? previewHash = null, string? password = null) => new()
    {
        { new StringContent(project.ToString()), "projectId" }, { new StringContent(hash), "manifestHash" },
        { new ByteArrayContent(bytes), "file", "integrity-source.zip" }, { new StringContent(JsonSerializer.Serialize(mapping, IntegritySourcePackage.Json)), "mapping" },
        { new StringContent(operation.ToString()), "operationId" }, { new StringContent(previewHash ?? ""), "previewHash" }, { new StringContent(password ?? ""), "password" },
    };
    internal static async Task Bootstrap(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap") { Content = JsonContent.Create(new
        { displayName = "Administrator", email = "admin@example.test", password = AeroLinkApiFactory.AdministratorPassword }) };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
        using var created = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode); await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }
}
