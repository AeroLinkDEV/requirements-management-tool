using System.IO.Compression;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class LegacyControlledProcedureDocumentSnapshotTests
{
    [Fact]
    public async Task A_pre_manifest_document_keeps_its_generation_time_revision_after_later_activity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var t0 = new DateTimeOffset(2026, 8, 10, 6, 0, 0, TimeSpan.Zero);
        var generatedAt = t0.AddHours(1);
        var program = new ProgramRecord("Legacy document snapshot", "LDS");
        var project = new ProjectRecord(program.Id, "Legacy document project", "Legacy document product");
        var release = new SoftwareRelease(project.Id, "4.0", false);
        var baseline = new CandidateBaseline("SW-40.00", 0, project.Id, release.Id, null,
            "Pre-manifest baseline", "cm", t0);

        var procedure = new TestProcedure(project.Id, "SYSTP-419900", "Current catalog title",
            "verification.engineer", t0, TestProcedureLevel.System);
        var tcr00 = Review(project.Id, release.Id, "SYSTCR-419900", 0,
            TestProcedureChangeKind.Introduce, "Generation-time title", t0);
        var revision00 = new TestProcedureRevision(procedure.Id, 0, "Generation-time objective",
            "Generation-time preconditions", "Generation-time steps", "Generation-time expected result",
            TestProcedureState.Approved, "verification.engineer", t0,
            sourceTestChangeRequestId: tcr00.Id);
        var document = new ControlledDocument(project.Id, release.Id, baseline.Id,
            ControlledDocumentType.SystemTestProcedures, "SYSTD-419900",
            "Legacy System Test Procedures", 0, new string('a', 64), 1, generatedAt);
        db.AddRange(program, project, release, baseline, procedure, tcr00, revision00, document);
        await db.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), $"aerolink-legacy-doc-{Guid.NewGuid():N}");
        try
        {
            var generator = new ControlledOutputGenerator(db,
                new RichContentPublisher(db, new EvidenceFileStore(root)));
            var first = await generator.GenerateAsync(document.Id, "docx", CancellationToken.None);
            Assert.NotNull(first);
            var firstXml = DocumentXml(first!);
            Assert.Contains("SYSTP-419900.00", firstXml);
            Assert.Contains("Generation-time title", firstXml);
            Assert.Contains("Generation-time objective", firstXml);
            Assert.Contains("Legacy generation-time compatibility snapshot", firstXml);

            // Later activity that used to rewrite the old document: a new approved revision appears and the
            // stable catalog title changes. The old document must remain bound to what existed at GeneratedAt.
            var t2 = generatedAt.AddHours(1);
            var tcr01 = Review(project.Id, release.Id, "SYSTCR-419901", 1,
                TestProcedureChangeKind.Modify, "Later title", t2);
            var revision01 = new TestProcedureRevision(procedure.Id, 1, "Later objective",
                "Later preconditions", "Later steps", "Later expected result",
                TestProcedureState.Approved, "verification.engineer", t2,
                sourceTestChangeRequestId: tcr01.Id);
            procedure.UpdateDraft("Later title", procedure.OwnerId, t2);
            db.AddRange(tcr01, revision01);
            await db.SaveChangesAsync();

            var second = await generator.GenerateAsync(document.Id, "docx", CancellationToken.None);
            Assert.NotNull(second);
            var secondXml = DocumentXml(second!);
            Assert.Equal(firstXml, secondXml);
            Assert.DoesNotContain("SYSTP-419900.01", secondXml);
            Assert.DoesNotContain("Later title", secondXml);
            Assert.DoesNotContain("Later objective", secondXml);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static TestChangeReview Review(Guid projectId, Guid releaseId, string number, int revision,
        TestProcedureChangeKind kind, string title, DateTimeOffset now)
    {
        var review = new TestChangeReview(projectId, releaseId, Guid.NewGuid(),
            TestChangeReviewDiscipline.System, $"SRCR-{419900 + revision:D6}.00", now,
            number, revision);
        review.RecordTestChangeRequired("verification.engineer", now);
        review.AddProcedureChange("verification.engineer", new TestProcedureChangeDraft(
            "SYSTP-419900", revision, TestProcedureLevel.System, kind, title,
            "Objective", "Preconditions", "Steps", "Expected result", "Rationale", "[]"), now);
        return review;
    }

    private static string DocumentXml(GeneratedOutput output)
    {
        using var archive = new ZipArchive(new MemoryStream(output.Content), ZipArchiveMode.Read);
        var entry = archive.GetEntry("word/document.xml");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }
}
