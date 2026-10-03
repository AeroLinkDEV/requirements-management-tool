using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

// Authoring gate: only a separate real runtime context detects live normalization before receipt lookup
// or a guard accidentally placed in historical SQLite callbacks. Pure codec tests cannot protect those owners.
public sealed class ProblemReportImportRuntimeCompatibilityTests
{
    private const string ChildFixture = "AEROLINK_PR_IMPORT_RUNTIME_FIXTURE";
    private sealed record Fixture(Guid Project, Guid Operation, AuthenticatedUser Actor, string PreviewHash,
        ProblemReportImportMapping Mapping, string Receipt);
    private static readonly byte[] Csv = Encoding.UTF8.GetBytes("Key,Title,Problem,Status,Category\nREPLAY,Original,Original problem,\u1c89,CodeFunctional\n");

    [Fact]
    public async Task Historical_reads_and_accepted_receipts_survive_runtime_drift_while_new_identity_work_is_refused()
    {
        var child = Environment.GetEnvironmentVariable(ChildFixture);
        if (child is not null)
        {
            Assert.True(StringComparer.OrdinalIgnoreCase.Equals("\u1c89", "\u1c8a"));
            var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(child + ".json"))!;
            await using var db = Context(child);
            await ProblemReportImportSqliteGuard.EnsureInstalledAsync(db);
            Assert.Equal("00001C8A", Convert.ToHexString(await db.ProblemReports.Where(x => x.SourceKey == "\u1c8a")
                .Select(x => EF.Property<byte[]>(x, "SourceKeyIdentityV1")).SingleAsync()));
            var service = new ProblemReportImportService(db);
            var replay = await service.CommitAsync(fixture.Project, fixture.Operation, Csv, "original.csv", fixture.Mapping,
                fixture.PreviewHash, fixture.Actor, "owned-runtime-fixture", _ => Task.FromResult(true), default);
            Assert.Equal(fixture.Receipt, JsonSerializer.Serialize(replay));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(fixture.Project,
                Csv, "new.csv", fixture.Mapping, fixture.Actor.UserName, default));
            Assert.Contains("writes are refused", refused.Message);
            db.Add(ProblemReportSourceIdentityDatabaseTests.Imported(fixture.Project, "PR-NEW", "Jira", "New"));
            Assert.Contains("writes are refused", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
            db.ChangeTracker.Clear();
            Assert.Equal(2, await db.ProblemReports.CountAsync());
            Assert.Single(await db.ProblemReportImportBatches.ToListAsync());
            return;
        }

        Assert.False(StringComparer.OrdinalIgnoreCase.Equals("\u1c89", "\u1c8a"));
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-pr-runtime-{Guid.NewGuid():N}.db");
        try
        {
            await using (var db = Context(path))
            {
                await db.Database.EnsureCreatedAsync();
                var project = await ProblemReportSourceIdentityDatabaseTests.SeedProject(db);
                var account = new UserAccount("importer", "Original actor", "actor@example.test", "fixture-password-hash", DateTimeOffset.UtcNow);
                db.Add(account); db.Add(ProblemReportSourceIdentityDatabaseTests.Imported(project, "PR-HISTORY", "Jira", "\u1c8a"));
                await db.SaveChangesAsync();
                var actor = new AuthenticatedUser(account.Id, account.UserName, account.DisplayName, account.Email, true, []);
                var mapping = new ProblemReportImportMapping { SourceSystem = "Jira",
                    Columns = new() { ["sourceKey"] = "Key", ["title"] = "Title", ["problem"] = "Problem", ["status"] = "Status", ["category"] = "Category" },
                    Statuses = new(StringComparer.Ordinal) { ["\u1c89"] = "ClosedInSource", ["\u1c8a"] = "Skip" } };
                var service = new ProblemReportImportService(db);
                var preview = await service.PreviewAsync(project, Csv, "original.csv", mapping, actor.UserName, default);
                var operation = Guid.NewGuid();
                var receipt = await service.CommitAsync(project, operation, Csv, "original.csv", mapping, preview.PreviewHash,
                    actor, "owned-runtime-fixture", _ => Task.FromResult(true), default);
                await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(new Fixture(project, operation, actor,
                    preview.PreviewHash, mapping, JsonSerializer.Serialize(receipt))));
            }
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (!File.Exists(start.FileName)) start.FileName = "dotnet";
            start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(ProblemReportImportRuntimeCompatibilityTests).Assembly.Location);
            start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" + typeof(ProblemReportImportRuntimeCompatibilityTests).FullName
                + ".Historical_reads_and_accepted_receipts_survive_runtime_drift_while_new_identity_work_is_refused");
            start.Environment[ChildFixture] = path; start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        finally { File.Delete(path); File.Delete(path + ".json"); }
    }

    private static AeroLinkDbContext Context(string path) => new(new DbContextOptionsBuilder<AeroLinkDbContext>()
        .UseSqlite($"Data Source={path};Pooling=False").Options);
}
