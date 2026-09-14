using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProjectInceptionSourceSealGuardTests
{
    [Fact]
    public async Task Cross_project_source_record_cannot_grant_unassigned_target_attribution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var sourceProgram = new ProgramRecord("Source program", "SRC");
        var targetProgram = new ProgramRecord("Target program", "TGT");
        var sourceProject = new ProjectRecord(sourceProgram.Id, "Source project", "Source product");
        var targetProject = new ProjectRecord(targetProgram.Id, "Target project", "Target product");
        var sourceRelease = new SoftwareRelease(sourceProject.Id, "1.0", true);
        var targetRelease = new SoftwareRelease(targetProject.Id, "1.0", true);
        db.AddRange(sourceProgram, targetProgram, sourceProject, targetProject, sourceRelease, targetRelease,
            LegacyDefaultProjectLadderFactory.Create(sourceProject.Id, now),
            LegacyDefaultProjectLadderFactory.Create(targetProject.Id, now));
        await db.SaveChangesAsync();

        var baseline = new CandidateBaseline("SW-01.30", 0, sourceProject.Id, sourceRelease.Id, null,
            "Source baseline", "source.manager", now);
        db.CandidateBaselines.Add(baseline);
        await db.SaveChangesAsync();

        var account = new UserAccount("source.admin", "Source Admin", "source.admin@example.test",
            IdentityService.HashPassword("source-admin-password"), now);
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        var draft = new ProjectSetupDraft(account.Id, account.UserName, "Target project setup");
        draft.BeginFinalization(draft.Version, "source-operation", now);
        draft.Complete("{}", sourceProgram.Id, sourceProject.Id, sourceRelease.Id, now);
        db.ProjectSetupDrafts.Add(draft);
        await db.SaveChangesAsync();

        var package = new ProjectSetupSourcePackage(draft.Id, ProjectSetupSourceKind.ExternalBaseline,
            "source.csv", "CSV", new string('a', 64), 1, [1], "source.admin", now);
        package.RecordAnalysis("source-tool", "{}", "{\"modules\":[]}", now);
        package.RecordConfiguration("[\"Procedures\"]", "{}", now);
        package.RecordReconciliation("{}", new string('b', 64), now);
        package.MarkMaterialized(sourceProject.Id, baseline.Id, new string('c', 64), now);

        // The target artifact is in a different project. A public source-record constructor must not turn this
        // cross-project row into evidence that an otherwise blank target owner was assigned by materialization.
        var targetProcedure = new TestProcedure(targetProject.Id, "SYSTP-00001", "Cross-project target", "", now,
            TestProcedureLevel.System);
        var fabricatedRecord = new ProjectInceptionSourceRecord(package.Id, sourceProject.Id, baseline.Id,
            "TestProcedure", targetProcedure.Id, null, "foreign-key", "module", "foreign-id", "1", "Frozen",
            "{\"source\":true}", now);
        db.AddRange(package, targetProcedure, fabricatedRecord);

        var error = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
        Assert.Contains("attributable actor", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var check = new AeroLinkDbContext(options);
        Assert.Empty(await check.TestProcedures.Where(x => x.Id == targetProcedure.Id).ToListAsync());
        Assert.Empty(await check.ProjectInceptionSourceRecords.Where(x => x.Id == fabricatedRecord.Id).ToListAsync());
    }
}
