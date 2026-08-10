using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class TestProcedureRevisionTitleProjectionTests
{
    [Fact]
    public async Task Exact_tcr_titles_survive_a_later_catalog_mutation_and_retirement_keeps_its_predecessor_title()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Revision title", "RTL");
        var project = new ProjectRecord(program.Id, "Title project", "Title product");
        var release = new SoftwareRelease(project.Id, "2.0", false);
        var change = new SystemChangeRequest("SRCR-04210", 0, project.Id, release.Id,
            "Title change", "Problem", "Analysis", "Solution", "author", now);

        var procedure = new TestProcedure(project.Id, "SYSTP-04210", "Verify legacy route sequencing",
            "verification.engineer", now, TestProcedureLevel.System);
        var introduce = Review(project.Id, release.Id, change.Id, change.DisplayNumber,
            "SYSTCR-04210", 0, TestProcedureChangeKind.Introduce,
            "Verify legacy route sequencing", now);
        var modify = Review(project.Id, release.Id, change.Id, change.DisplayNumber,
            "SYSTCR-04210", 1, TestProcedureChangeKind.Modify,
            "Verify route sequencing and discontinuities", now.AddMinutes(1));
        var retire = Review(project.Id, release.Id, change.Id, change.DisplayNumber,
            "SYSTCR-04210", 2, TestProcedureChangeKind.Retire, "", now.AddMinutes(2));

        var revision00 = Revision(procedure.Id, 0, introduce.Id, now);
        var revision01 = Revision(procedure.Id, 1, modify.Id, now.AddMinutes(1));
        var revision02 = new TestProcedureRevision(procedure.Id, 2, "", "", "", "",
            TestProcedureState.Retired, "verification.engineer", now.AddMinutes(2),
            sourceTestChangeRequestId: retire.Id);

        // Reproduce the old global mutation: a later Modify rewrote the stable catalog title. Exact revision
        // title projection must remain anchored to each immutable TCR change snapshot despite that mutation.
        procedure.UpdateDraft("Verify route sequencing and discontinuities", procedure.OwnerId, now.AddMinutes(3));
        db.AddRange(program, project, release, change, procedure, introduce, modify, retire,
            revision00, revision01, revision02);
        await db.SaveChangesAsync();

        var result = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
            [revision00.Id, revision01.Id, revision02.Id], CancellationToken.None);

        Assert.Equal("Verify legacy route sequencing", result[revision00.Id].Title);
        Assert.True(result[revision00.Id].IsExact);
        Assert.Equal("Verify route sequencing and discontinuities", result[revision01.Id].Title);
        Assert.True(result[revision01.Id].IsExact);
        Assert.Equal("Verify route sequencing and discontinuities", result[revision02.Id].Title);
        Assert.True(result[revision02.Id].IsExact);
        Assert.Contains("Retirement revision", result[revision02.Id].Note);
    }

    [Fact]
    public async Task Legacy_revision_uses_a_deterministic_label_with_truthful_compatibility_wording()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Legacy title", "LTL");
        var project = new ProjectRecord(program.Id, "Legacy project", "Legacy product");
        var procedure = new TestProcedure(project.Id, "SYSTP-04211", "Legacy catalog title",
            "legacy.author", now, TestProcedureLevel.System);
        var revision = Revision(procedure.Id, 0, null, now);
        db.AddRange(program, project, procedure, revision);
        await db.SaveChangesAsync();

        var result = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
            [revision.Id], CancellationToken.None);
        var title = result[revision.Id];

        Assert.Equal("Legacy procedure SYSTP-04211.00 — exact historical title was not recorded", title.Title);
        Assert.False(title.IsExact);
        Assert.True(title.IsLegacy);
        Assert.Contains("Legacy revision", title.Note);
        Assert.Contains("compatibility", title.Note);
    }

    private static TestChangeReview Review(Guid projectId, Guid releaseId, Guid changeRequestId,
        string changeRequestNumber, string baseNumber, int revision, TestProcedureChangeKind kind,
        string title, DateTimeOffset now)
    {
        var review = new TestChangeReview(projectId, releaseId, changeRequestId,
            TestChangeReviewDiscipline.System, changeRequestNumber, now, baseNumber, revision);
        review.RecordTestChangeRequired("verification.engineer", now);
        review.AddProcedureChange("verification.engineer", new TestProcedureChangeDraft("SYSTP-04210",
            revision, TestProcedureLevel.System, kind, title,
            kind == TestProcedureChangeKind.Retire ? "" : "Verify exact revision title.",
            kind == TestProcedureChangeKind.Retire ? "" : "Configured system.",
            kind == TestProcedureChangeKind.Retire ? "" : "Exercise the procedure.",
            kind == TestProcedureChangeKind.Retire ? "" : "Expected behavior is observed.",
            "Revision title fixture.", "[]"), now);
        return review;
    }

    private static TestProcedureRevision Revision(Guid procedureId, int revision, Guid? sourceTcrId,
        DateTimeOffset now) => new(procedureId, revision, "Verify title.", "Configured system.",
        "Exercise the procedure.", "Expected behavior is observed.", TestProcedureState.Approved,
        "verification.engineer", now, sourceTestChangeRequestId: sourceTcrId);
}
