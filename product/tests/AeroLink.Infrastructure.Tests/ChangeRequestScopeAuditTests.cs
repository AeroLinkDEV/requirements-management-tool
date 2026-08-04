using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A change request may not hold a requirement level its type forbids, and a survivor must be reportable.
///
/// The domain has refused the combination since #275, but enforcement only guards records being written. The
/// persistent database kept `SRCR-00032.00` carrying `HLR-000075.02` through the fix, a closed issue and a
/// handoff claiming it was corrected — because the correction was made to the seeder, which never runs against
/// an existing database. It was eventually noticed in a screenshot.
///
/// These tests hold the reporting half. The repair itself is a PostgreSQL migration, exercised by the
/// migration job rather than here: SQLite databases are created from the current model and never contain a row
/// that predates the rule, so a SQLite test of the repair would be asserting against data it had to smuggle in
/// past the very rule under test.
/// </summary>
public sealed class ChangeRequestScopeAuditTests
{
    private static async Task<(AeroLinkDbContext Db, Guid ProjectId, Guid ReleaseId)> DatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Scope Audit Program", "SAP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Scope Audit Software");
        var release = new SoftwareRelease(project.Id, "1.6", false);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync();
        return (db, project.Id, release.Id);
    }

    private static SystemChangeRequest Request(Guid projectId, Guid releaseId, string number, ChangeRequestType type, RequirementLevel? softwareLevel = null) =>
        new(number, 0, projectId, releaseId, $"{type} change", "P", "A", "S", "author", DateTimeOffset.UtcNow, type, softwareLevel: type == ChangeRequestType.Software ? softwareLevel ?? RequirementLevel.HighLevel : null);

    [Fact]
    public async Task A_compliant_database_reports_no_violation()
    {
        var (db, projectId, releaseId) = await DatabaseAsync();
        await using var _ = db;
        var now = DateTimeOffset.UtcNow;

        var system = Request(projectId, releaseId, "SRCR-70001", ChangeRequestType.System);
        system.AddRequirementChange("author", "SYSR-000001", 0, RequirementLevel.System, RequirementChangeKind.Introduce,
            "The system shall do the controlled thing.", "Because.", "Test", now);
        var software = Request(projectId, releaseId, "HLRCR-70001", ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        software.AddRequirementChange("author", "HLR-000001", 0, RequirementLevel.HighLevel, RequirementChangeKind.Introduce,
            "The software shall do the controlled thing.", "Because.", "Test", now);
        db.AddRange(system, software);
        await db.SaveChangesAsync();

        Assert.Empty(await ChangeRequestScopeAudit.ViolationsAsync(db));
    }

    /// <summary>
    /// The audit has to find a row the product can no longer create, so the row is written the only way it
    /// could have arrived — straight into the table, exactly as a legacy record or a future import would.
    /// </summary>
    [Fact]
    public async Task A_System_request_holding_an_HLR_change_is_reported()
    {
        var (db, projectId, releaseId) = await DatabaseAsync();
        await using var _ = db;
        var now = DateTimeOffset.UtcNow;

        var system = Request(projectId, releaseId, "SRCR-00032", ChangeRequestType.System);
        db.Add(system);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO requirement_changes
                ("Id","ScrId","BaseNumber","Revision","Level","Kind","Statement","Rationale","VerificationMethod",
                 "RichText","AttributesJson","ImpactDispositionJson","ProposedUpstreamRevisionIdsJson")
            VALUES ({0},{1},'HLR-000075',2,'HighLevel','Modify','The software shall hold the clarified behavior.',
                    'Legacy contamination.','Test','','{{}}','{{}}','[]')
            """, Guid.NewGuid(), system.Id);

        var violations = await ChangeRequestScopeAudit.ViolationsAsync(db);

        var violation = Assert.Single(violations);
        Assert.Equal("SRCR-00032", violation.ChangeRequestNumber);
        Assert.Equal("System", violation.ChangeRequestType);
        Assert.Equal("HLR-000075", violation.RequirementNumber);
        Assert.Equal("HighLevel", violation.RequirementLevel);
    }

    [Fact]
    public async Task A_Software_request_holding_a_System_change_is_reported()
    {
        var (db, projectId, releaseId) = await DatabaseAsync();
        await using var _ = db;

        var software = Request(projectId, releaseId, "HLRCR-70002", ChangeRequestType.Software, softwareLevel: RequirementLevel.HighLevel);
        db.Add(software);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO requirement_changes
                ("Id","ScrId","BaseNumber","Revision","Level","Kind","Statement","Rationale","VerificationMethod",
                 "RichText","AttributesJson","ImpactDispositionJson","ProposedUpstreamRevisionIdsJson")
            VALUES ({0},{1},'SYSR-000900',0,'System','Introduce','The system shall do the wrongly placed thing.',
                    'Legacy contamination.','Test','','{{}}','{{}}','[]')
            """, Guid.NewGuid(), software.Id);

        var violation = Assert.Single(await ChangeRequestScopeAudit.ViolationsAsync(db));
        Assert.Equal("Software", violation.ChangeRequestType);
        Assert.Equal("System", violation.RequirementLevel);
    }

    /// <summary>
    /// The rule the domain enforces and the rule the audit reports have to be the same rule, or a repair could
    /// leave behind exactly the rows the product would refuse to write.
    /// </summary>
    [Fact]
    public void The_audit_and_the_domain_agree_on_every_combination()
    {
        foreach (var level in Enum.GetValues<RequirementLevel>())
        {
            Assert.Equal(level == RequirementLevel.System,
                SystemChangeRequest.AcceptsRequirementLevel(ChangeRequestType.System, level));
            Assert.Equal(level != RequirementLevel.System,
                SystemChangeRequest.AcceptsRequirementLevel(ChangeRequestType.Software, level));
        }
    }
}
