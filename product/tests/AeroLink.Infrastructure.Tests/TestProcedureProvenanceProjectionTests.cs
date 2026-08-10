using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class TestProcedureProvenanceProjectionTests
{
    [Fact]
    public async Task Folded_sources_keep_each_exact_change_request_and_the_exact_package_revision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = fixture.Now;
        var crA = ChangeRequest("SRCR-04240", fixture.Project.Id, fixture.Release.Id, "Primary", now);
        var crB = ChangeRequest("SRCR-04241", fixture.Project.Id, fixture.Release.Id, "Folded", now);
        var changeA = AddRequirement(crA, "SYSR-04240", now);
        var changeB = AddRequirement(crB, "SYSR-04241", now);
        var tcr = new TestChangeReview(fixture.Project.Id, fixture.Release.Id, crA.Id,
            TestChangeReviewDiscipline.System, crA.DisplayNumber, now,
            baseNumber: "SYSTCR-04240", revision: 1);
        tcr.IncludeChangeRequest("verification.engineer", crB.Id, crB.DisplayNumber, now);
        var procedure = new TestProcedure(fixture.Project.Id, "SYSTP-04240", "Folded source procedure",
            "verification.engineer", now, TestProcedureLevel.System);
        var revision = Revision(procedure.Id, 1, tcr.Id, now);
        var impactA = VerificationImpactItem.ForIntroducedRequirement(fixture.Project.Id, fixture.Release.Id,
            crA.Id, tcr.Id, changeA.Id, changeA.DisplayNumber, "Test", now);
        var impactB = VerificationImpactItem.ForIntroducedRequirement(fixture.Project.Id, fixture.Release.Id,
            crB.Id, tcr.Id, changeB.Id, changeB.DisplayNumber, "Test", now);
        impactA.Resolve("verification.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Primary source is verified.", now, procedure.Id, revision.Id,
            TestProcedureChangeAction.ModifyExisting);
        impactB.Resolve("verification.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Folded source is verified.", now, procedure.Id, revision.Id,
            TestProcedureChangeAction.ModifyExisting);

        fixture.Db.AddRange(crA, crB, tcr, procedure, revision, impactA, impactB);
        await fixture.Db.SaveChangesAsync();

        var result = await TestProcedureProvenanceProjection.ForRevisionsAsync(
            fixture.Db, [revision.Id], CancellationToken.None);
        var provenance = result[revision.Id];

        Assert.Equal(tcr.Id, provenance.SourceTestChangeRequestId);
        Assert.Equal("SYSTCR-04240.01", provenance.Package);
        Assert.False(provenance.IsLegacy);
        Assert.Null(provenance.Note);
        Assert.Equal(2, provenance.Drivers.Count);
        Assert.All(provenance.Drivers, row => Assert.Equal("SYSTCR-04240.01", row.Package));
        Assert.Equal(new[] { "SRCR-04240.00", "SRCR-04241.00" },
            provenance.Drivers.Select(x => x.ChangeRequest).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { changeA.DisplayNumber, changeB.DisplayNumber },
            provenance.Drivers.Select(x => x.SubjectDisplayNumber).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Manual_tcr_without_impacts_keeps_the_package_and_all_claimed_source_changes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = fixture.Now;
        var primary = ChangeRequest("SRCR-04250", fixture.Project.Id, fixture.Release.Id, "Manual primary", now);
        var folded = ChangeRequest("SRCR-04251", fixture.Project.Id, fixture.Release.Id, "Manual folded", now);
        var tcr = new TestChangeReview(fixture.Project.Id, fixture.Release.Id, primary.Id,
            TestChangeReviewDiscipline.System, primary.DisplayNumber, now,
            baseNumber: "SYSTCR-04250", revision: 0);
        tcr.IncludeChangeRequest("verification.engineer", folded.Id, folded.DisplayNumber, now);
        var procedure = new TestProcedure(fixture.Project.Id, "SYSTP-04250", "Manual package procedure",
            "verification.engineer", now, TestProcedureLevel.System);
        var revision = Revision(procedure.Id, 0, tcr.Id, now);

        fixture.Db.AddRange(primary, folded, tcr, procedure, revision);
        await fixture.Db.SaveChangesAsync();

        var result = await TestProcedureProvenanceProjection.ForRevisionsAsync(
            fixture.Db, [revision.Id], CancellationToken.None);
        var provenance = result[revision.Id];

        Assert.Equal("SYSTCR-04250.00", provenance.Package);
        Assert.Equal(2, provenance.Drivers.Count);
        Assert.All(provenance.Drivers, row =>
        {
            Assert.Equal("SYSTCR-04250.00", row.Package);
            Assert.Equal("PackageSource", row.Action);
            Assert.Equal("", row.SubjectDisplayNumber);
            Assert.False(row.IsLegacy);
        });
        Assert.Equal(new[] { "SRCR-04250.00", "SRCR-04251.00" },
            provenance.Drivers.Select(x => x.ChangeRequest).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Legacy_revision_remains_readable_without_an_invented_package()
    {
        await using var fixture = await Fixture.CreateAsync();
        var procedure = new TestProcedure(fixture.Project.Id, "SYSTP-04260", "Legacy procedure",
            "legacy.author", fixture.Now, TestProcedureLevel.System);
        var revision = Revision(procedure.Id, 0, sourceTcrId: null, fixture.Now);
        fixture.Db.AddRange(procedure, revision);
        await fixture.Db.SaveChangesAsync();

        var result = await TestProcedureProvenanceProjection.ForRevisionsAsync(
            fixture.Db, [revision.Id], CancellationToken.None);
        var provenance = result[revision.Id];

        Assert.Null(provenance.SourceTestChangeRequestId);
        Assert.Null(provenance.Package);
        Assert.True(provenance.IsLegacy);
        Assert.Contains("Legacy revision", provenance.Note);
        Assert.Empty(provenance.Drivers);
    }

    [Fact]
    public async Task Legacy_revision_keeps_exact_related_impact_without_calling_it_the_producing_package()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = ChangeRequest("SRCR-04261", fixture.Project.Id, fixture.Release.Id,
            "Known related impact", fixture.Now);
        var change = AddRequirement(source, "SYSR-04261", fixture.Now);
        var tcr = new TestChangeReview(fixture.Project.Id, fixture.Release.Id, source.Id,
            TestChangeReviewDiscipline.System, source.DisplayNumber, fixture.Now,
            baseNumber: "SYSTCR-04261", revision: 2);
        var procedure = new TestProcedure(fixture.Project.Id, "SYSTP-04261", "Legacy procedure",
            "legacy.author", fixture.Now, TestProcedureLevel.System);
        var revision = Revision(procedure.Id, 0, sourceTcrId: null, fixture.Now);
        var impact = VerificationImpactItem.ForIntroducedRequirement(fixture.Project.Id, fixture.Release.Id,
            source.Id, tcr.Id, change.Id, change.DisplayNumber, "Test", fixture.Now);
        impact.Resolve("verification.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
            "Known later relationship.", fixture.Now, procedure.Id, revision.Id,
            TestProcedureChangeAction.ModifyExisting);
        fixture.Db.AddRange(source, tcr, procedure, revision, impact);
        await fixture.Db.SaveChangesAsync();

        var result = await TestProcedureProvenanceProjection.ForRevisionsAsync(
            fixture.Db, [revision.Id], CancellationToken.None);
        var provenance = result[revision.Id];

        Assert.Null(provenance.SourceTestChangeRequestId);
        Assert.Null(provenance.Package);
        Assert.True(provenance.IsLegacy);
        Assert.Contains("producing test change request was not recorded", provenance.Note);
        var related = Assert.Single(provenance.Drivers);
        Assert.True(related.IsLegacy);
        Assert.Equal("SYSTCR-04261.02", related.Package);
        Assert.Equal("SRCR-04261.00", related.ChangeRequest);
        Assert.Equal(change.DisplayNumber, related.SubjectDisplayNumber);
    }

    private static SystemChangeRequest ChangeRequest(string number, Guid projectId, Guid releaseId,
        string title, DateTimeOffset now) =>
        new(number, 0, projectId, releaseId, title, "Problem", "Analysis", "Solution", "author", now);

    private static RequirementChange AddRequirement(SystemChangeRequest request, string number,
        DateTimeOffset now) =>
        request.AddRequirementChange("author", number, 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, $"The product shall satisfy {number}.",
            "Provenance fixture", "Test", now);

    private static TestProcedureRevision Revision(Guid procedureId, int number, Guid? sourceTcrId,
        DateTimeOffset now) =>
        new(procedureId, number, "Verify exact provenance.", "The build is available.",
            "1. Exercise the controlled behavior.", "The expected behavior is observed.",
            TestProcedureState.Approved, "verification.engineer", now,
            sourceTestChangeRequestId: sourceTcrId);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, AeroLinkDbContext db, ProgramRecord program,
            ProjectRecord project, SoftwareRelease release, DateTimeOffset now)
        {
            _connection = connection;
            Db = db;
            Program = program;
            Project = project;
            Release = release;
            Now = now;
        }

        public AeroLinkDbContext Db { get; }
        public ProgramRecord Program { get; }
        public ProjectRecord Project { get; }
        public SoftwareRelease Release { get; }
        public DateTimeOffset Now { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = new DateTimeOffset(2026, 8, 10, 2, 0, 0, TimeSpan.Zero);
            var program = new ProgramRecord("Procedure provenance", "PRV");
            var project = new ProjectRecord(program.Id, "Provenance project", "Provenance product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            db.AddRange(program, project, release);
            await db.SaveChangesAsync();
            return new(connection, db, program, project, release, now);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
