using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

// Authoring gate: the HTTP boundary owns replay/password/status and durable transaction publication.
// Existing import coverage never replays a committed identity or fails a signature write. The command
// interceptor injects a failure at a real provider boundary; no production test seam is required.
public sealed class ProblemReportImportRecoveryApiTests
{
    internal const string Root = "/api/problem-reports/import";
    internal const string Csv = "Key,Summary,Description,Status,Category,Version\nPR-1,First,First problem,Closed,CodeFunctional,1.0\nPR-2,Second,Second problem,Closed,CodeFunctional,1.0\n";

    [Fact]
    public async Task A_committed_operation_replays_its_original_receipt_and_refuses_changed_content()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportImportApiTests.BootstrapAsync(client);
        var (project, build) = await SeedProject(factory.Services);
        var operation = Guid.NewGuid();
        const string editableCsv = "Key,Summary,Description,Status,Category,Version,Owner\nPR-1,First,First problem,Draft,CodeFunctional,1.0,admin\nPR-2,Second,Second problem,Draft,CodeFunctional,1.0,admin\n";
        var hash = await Preview(client, project, build, editableCsv);
        using var committed = await client.PostAsync(Root + "/commit", Form(project, build, operation, hash, csv: editableCsv));
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var original = await committed.Content.ReadAsStringAsync();
        using (var change = factory.Services.CreateScope())
        {
            var store = change.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var report = await store.ProblemReports.SingleAsync(x => x.ProjectId == project && x.SourceKey == "PR-1");
            var originalNumber = report.DisplayNumber;
            var originalRevision = report.Revision;
            // Owned fixture advances the existing aggregate and appends evidence; the imported event remains immutable.
            foreach (var state in new[] { ProblemReportState.Rejected, ProblemReportState.Draft })
            {
                var now = DateTimeOffset.UtcNow;
                report.TransitionTo(state, "admin", "Later engineering decision", now);
                var evidence = await ProblemReportAttachmentEvidence.SnapshotAsync(store, report, default);
                store.Add(new ProblemReportRevision(report.Id, report.Revision, "LaterEngineeringDecision", "admin",
                    evidence.Hash, evidence.Json, now, toState: state.ToString()));
                await store.SaveChangesAsync();
            }
            Assert.NotEqual(originalNumber, report.DisplayNumber);
            Assert.Equal(originalRevision + 1, report.Revision);
        }
        using var replay = await client.PostAsync(Root + "/commit", Form(project, build, operation, hash, csv: editableCsv));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());

        using var wrongPassword = await client.PostAsync(Root + "/commit",
            Form(project, build, operation, hash, csv: editableCsv, password: "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        using var changed = await client.PostAsync(Root + "/commit",
            Form(project, build, operation, hash, csv: editableCsv.Replace("First problem", "Changed problem")));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var changedName = await client.PostAsync(Root + "/commit",
            Form(project, build, operation, hash, csv: editableCsv, fileName: "other.csv"));
        Assert.Equal(HttpStatusCode.Conflict, changedName.StatusCode);
        using var changedHash = await client.PostAsync(Root + "/commit",
            Form(project, build, operation, new string('0', 64), csv: editableCsv));
        Assert.Equal(HttpStatusCode.Conflict, changedHash.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(2, await db.ProblemReports.CountAsync(x => x.ProjectId == project));
        Assert.Equal(4, await db.ProblemReportRevisions.CountAsync());
        Assert.Equal(2, await db.ProblemReportLinks.CountAsync());
        Assert.Single(await db.ProblemReportImportBatches.Where(x => x.ProjectId == project).ToListAsync());
        Assert.Single(await db.ElectronicSignatures.Where(x => x.Action == "ImportProblemReports").ToListAsync());

        // Historical PreviewHash uses lowercased keys. This known collision stays a B1 policy fact,
        // while the new operation fingerprint must refuse the changed (OrdinalIgnoreCase-distinct) mapping.
        Assert.False(StringComparer.OrdinalIgnoreCase.Equals("K", "K"));
        var collisionCsv = Csv.Replace("PR-1", "PR-3").Replace("PR-2", "PR-4").Replace(",Closed,", ",K,");
        var collisionHash = await Preview(client, project, build, collisionCsv, statusKey: "K");
        var collisionOperation = Guid.NewGuid();
        using var collisionCommit = await client.PostAsync(Root + "/commit", Form(project, build, collisionOperation,
            collisionHash, csv: collisionCsv, statusKey: "K"));
        Assert.Equal(HttpStatusCode.OK, collisionCommit.StatusCode);
        Assert.Equal(collisionHash, await Preview(client, project, build, collisionCsv, statusKey: "K"));
        using var collisionReplay = await client.PostAsync(Root + "/commit", Form(project, build, collisionOperation,
            collisionHash, csv: collisionCsv, statusKey: "K"));
        Assert.Equal(HttpStatusCode.Conflict, collisionReplay.StatusCode);
    }

    [Fact]
    public async Task Accepted_operation_receipts_are_scoped_to_project_and_actor_and_replay_checks_current_authority()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportImportApiTests.BootstrapAsync(client);
        var (project, build) = await SeedProject(factory.Services);
        var operation = Guid.NewGuid();
        var hash = await Preview(client, project, build);
        using var committed = await client.PostAsync(Root + "/commit", Form(project, build, operation, hash));
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var original = await committed.Content.ReadFromJsonAsync<JsonElement>();
        var (otherProject, otherBuild) = await SeedProject(factory.Services);
        var otherHash = await Preview(client, otherProject, otherBuild);
        using var otherCommit = await client.PostAsync(Root + "/commit", Form(otherProject, otherBuild, operation, otherHash));
        Assert.Equal(HttpStatusCode.OK, otherCommit.StatusCode);
        Assert.NotEqual(original.GetProperty("batchId").GetGuid(),
            (await otherCommit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("batchId").GetGuid());

        const string userName = "recovery.manager";
        Guid managerId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var manager = new UserAccount(userName, "Configuration authority", "recovery@example.test",
                IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), DateTimeOffset.UtcNow);
            managerId = manager.Id;
            var programId = await db.Projects.Where(x => x.Id == project).Select(x => x.ProgramId).SingleAsync();
            db.AddRange(manager, new ProgramMembership(manager.Id, programId, ProgramRole.Administrator, "admin", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        using var managerClient = factory.CreateClient();
        using (var login = await managerClient.PostAsJsonAsync("/api/auth/login", new { userName, password = AeroLinkApiFactory.MemberPassword }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(managerClient);
        using var differentActor = await managerClient.PostAsync(Root + "/commit",
            Form(project, build, operation, hash, password: AeroLinkApiFactory.MemberPassword));
        // The other actor's operation has no receipt; the normal re-preview finds nothing to create.
        Assert.Equal(HttpStatusCode.BadRequest, differentActor.StatusCode);
        var managerCsv = Csv.Replace("PR-1", "PR-3").Replace("PR-2", "PR-4");
        var managerHash = await Preview(managerClient, project, build, managerCsv);
        using var managerCommit = await managerClient.PostAsync(Root + "/commit",
            Form(project, build, operation, managerHash, csv: managerCsv, password: AeroLinkApiFactory.MemberPassword));
        Assert.Equal(HttpStatusCode.OK, managerCommit.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            foreach (var membership in await db.ProgramMemberships.Where(x => x.UserId == managerId).ToListAsync())
                membership.End("admin", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        using var revoked = await managerClient.PostAsync(Root + "/commit",
            Form(project, build, operation, managerHash, csv: managerCsv, password: AeroLinkApiFactory.MemberPassword));
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var features = new ProjectFeatureSet(project, ProjectFeatures.All & ~ProjectFeature.ProblemReports,
                "admin", DateTimeOffset.UtcNow);
            db.Add(features);
            await db.SaveChangesAsync();
        }
        using var disabled = await client.PostAsync(Root + "/commit", Form(project, build, operation, hash));
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Equal("feature_disabled", (await disabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unsigned_detection_correlates_batch_type_identity_and_action_without_repairing_legacy_rows()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        await ProblemReportImportApiTests.BootstrapAsync(client);
        var (project, build) = await SeedProject(factory.Services);
        var hash = await Preview(client, project, build);
        using var committed = await client.PostAsync(Root + "/commit", Form(project, build, Guid.NewGuid(), hash));
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        Guid wrongType, wrongAction;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var actor = await db.UserAccounts.SingleAsync(x => x.UserName == "admin");
            var programId = await db.Projects.Where(x => x.Id == project).Select(x => x.ProgramId).SingleAsync();
            var a = new ProblemReportImportBatch(project, "Legacy", "a.csv", hash, "{}", hash, 1, 0, "legacy", DateTimeOffset.UtcNow);
            var b = new ProblemReportImportBatch(project, "Legacy", "b.csv", hash, "{}", hash, 1, 0, "legacy", DateTimeOffset.UtcNow);
            wrongType = a.Id; wrongAction = b.Id;
            db.AddRange(a, b,
                new ElectronicSignature(actor.Id, "admin", "Administrator", programId, "ProblemReport", a.Id,
                    "a.csv", "ImportProblemReports", "Wrong type control", hash, "local", DateTimeOffset.UtcNow),
                new ElectronicSignature(actor.Id, "admin", "Administrator", programId, "ProblemReportImportBatch", b.Id,
                    "b.csv", "OtherAction", "Wrong action control", hash, "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var unsigned = await client.GetFromJsonAsync<JsonElement>($"{Root}/batches?projectId={project}&unsignedOnly=true");
        Assert.Equal(new[] { wrongType, wrongAction }.Order(), unsigned.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Order());
        Assert.All(unsigned.EnumerateArray(), x => Assert.False(x.GetProperty("hasImportSignature").GetBoolean()));
        using var check = factory.Services.CreateScope();
        var final = check.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.Equal(3, await final.ElectronicSignatures.CountAsync());
        Assert.All(await final.ProblemReportImportBatches.Where(x => x.SourceSystem == "Legacy").ToListAsync(), x =>
        {
            Assert.Null(x.OperationId); Assert.Null(x.ActorId); Assert.Null(x.ReceiptJson);
            Assert.Equal(hash, x.PreviewHash); Assert.Equal("legacy", x.ImportedBy);
        });
    }

    [Fact]
    public async Task Failure_writing_the_signature_rolls_back_every_import_record_and_retry_can_complete()
    {
        var fault = new SignatureWriteFailure();
        using var factory = new AeroLinkApiFactory(commandInterceptor: fault);
        using var client = factory.CreateClient();
        await ProblemReportImportApiTests.BootstrapAsync(client);
        var (project, build) = await SeedProject(factory.Services);
        var hash = await Preview(client, project, build);
        var operation = Guid.NewGuid();
        fault.Armed = true;
        using var failed = await client.PostAsync(Root + "/commit", Form(project, build, operation, hash));
        Assert.False(failed.IsSuccessStatusCode);
        Assert.True(fault.Observed);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            Assert.False(await db.ProblemReports.AnyAsync(x => x.ProjectId == project));
            Assert.False(await db.ProblemReportRevisions.AnyAsync());
            Assert.False(await db.ProblemReportLinks.AnyAsync());
            Assert.False(await db.ProblemReportImportBatches.AnyAsync(x => x.ProjectId == project));
            Assert.False(await db.ElectronicSignatures.AnyAsync(x => x.Action == "ImportProblemReports"));
        }
        using var retry = await client.PostAsync(Root + "/commit", Form(project, build, operation, hash));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    internal static async Task<(Guid Project, Guid Build)> SeedProject(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var program = new ProgramRecord("Import recovery", "R" + Guid.NewGuid().ToString("N")[..20]);
        var project = new ProjectRecord(program.Id, "Destination", "PR");
        var build = new SoftwareRelease(project.Id, "1.0", false);
        db.AddRange(program, project, build);
        await db.SaveChangesAsync();
        return (project.Id, build.Id);
    }

    internal static async Task<string> Preview(HttpClient client, Guid project, Guid build, string csv = Csv, string statusKey = "Closed")
    {
        using var response = await client.PostAsync(Root + "/preview", Form(project, build, Guid.NewGuid(), csv: csv, statusKey: statusKey));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("previewHash").GetString()!;
    }

    internal static MultipartFormDataContent Form(Guid project, Guid build, Guid operation, string hash = "",
        string csv = Csv, string password = AeroLinkApiFactory.AdministratorPassword, string fileName = "reports.csv", string statusKey = "Closed") => new()
    {
        { new StringContent(project.ToString()), "projectId" },
        { new StringContent(operation.ToString()), "operationId" },
        { new StringContent(hash), "previewHash" },
        { new StringContent(password), "password" },
        { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", fileName },
        { new StringContent(JsonSerializer.Serialize(new
        {
            sourceSystem = "Jira",
            columns = new Dictionary<string, string> { ["sourceKey"] = "Key", ["title"] = "Summary",
                ["problem"] = "Description", ["status"] = "Status", ["category"] = "Category", ["targetBuild"] = "Version",
                ["responsibleEngineer"] = "Owner" },
            statuses = new Dictionary<string, string> { [statusKey] = "ClosedInSource", ["Draft"] = "Draft" },
            people = new Dictionary<string, string> { ["admin"] = "admin" },
            builds = new Dictionary<string, string> { ["1.0"] = build.ToString() },
        })), "mapping" },
    };

    private sealed class SignatureWriteFailure : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Observed { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("INSERT INTO \"electronic_signatures\"", StringComparison.Ordinal))
            {
                Armed = false; Observed = true;
                throw new IOException("Injected signature persistence failure.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
