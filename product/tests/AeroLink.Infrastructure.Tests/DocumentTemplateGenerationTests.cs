using System.IO.Compression;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Templates were already controlled, numbered, approved and versioned — and read by nothing, so approving
/// one changed no document. What is asserted here is that an approved layout now decides what a controlled
/// document actually contains, that a document keeps the layout it was generated with after the template is
/// revised, and that a project without a layout is unaffected.
/// </summary>
[Collection(ShowcaseCollection.Name)]
public sealed class DocumentTemplateGenerationTests(ShowcaseDatabaseFixture showcaseFixture)
{
    private const string Layout = """
        {
          "appliesTo": "Sysrd",
          "titlePattern": "{product} Aircraft-Level Requirements",
          "subtitlePattern": "Issued against baseline {baseline}",
          "sections": [
            { "heading": "Section 1 - Verification Basis", "introduction": "Coverage for every published requirement.", "content": "VerificationAnnex" },
            { "heading": "Section 2 - Requirements", "introduction": "{recordCount} controlled records.", "content": "ControlledRecords" }
          ]
        }
        """;

    private static ControlledOutputGenerator Generator(AeroLinkDbContext db) =>
        new(db, new RichContentPublisher(db, new EvidenceFileStore(Path.Combine(Path.GetTempPath(), $"aerolink-evidence-{Guid.NewGuid():N}"))));

    private static async Task<string> DocumentXmlAsync(GeneratedOutput output)
    {
        using var archive = new ZipArchive(new MemoryStream(output.Content), ZipArchiveMode.Read);
        var part = archive.GetEntry("word/document.xml");
        Assert.NotNull(part);
        using var reader = new StreamReader(part!.Open());
        return await reader.ReadToEndAsync();
    }

    private static async Task<DocumentTemplateRevision> ApproveAsync(AeroLinkDbContext db, Guid projectId, string body, int revision = 1)
    {
        var template = new DocumentTemplate(projectId, "TMPL-000001", "Aircraft-level SYSRD layout", body, "config.manager", DateTimeOffset.UtcNow);
        db.DocumentTemplates.Add(template);
        var number = template.Approve("config.manager", DateTimeOffset.UtcNow);
        var evidence = new DocumentTemplateRevision(template.Id, number, "Sysrd", "ACME Aerospace", body,
            new string('a', 64), "config.manager", DateTimeOffset.UtcNow);
        db.DocumentTemplateRevisions.Add(evidence);
        await db.SaveChangesAsync();
        return evidence;
    }

    // A copy of the showcase rather than a fresh seed. Each of the four tests here spent between 36 and 65
    // seconds rebuilding an identical 1,250-requirement dataset; they now take a file copy of one built once for
    // the whole run. The database each test receives is still private and still writable.
    private async Task<(DbContextOptions<AeroLinkDbContext> Options, Guid ProjectId, Guid ReleaseId, Guid BaselineId, string Path)> SeedAsync()
    {
        var showcase = showcaseFixture.Create();
        await using var db = showcase.Context();
        var summary = showcaseFixture.Summary;
        var document = await db.ControlledDocuments.AsNoTracking()
            .FirstAsync(x => x.BaselineId == summary.ReleasedBaselineId && x.Type == ControlledDocumentType.Sysrd);
        return (showcase.Options, summary.ProjectId, document.ReleaseId, summary.ReleasedBaselineId, showcase.Path);
    }

    [Fact]
    public async Task An_approved_layout_decides_what_the_document_contains()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var revision = await ApproveAsync(db, seed.ProjectId, Layout);
            var document = new ControlledDocument(seed.ProjectId, seed.ReleaseId, seed.BaselineId,
                ControlledDocumentType.Sysrd, "SYSRD-000900", "Fallback title", 0, new string('b', 64), 12,
                DateTimeOffset.UtcNow, revision.Id);
            db.ControlledDocuments.Add(document);
            await db.SaveChangesAsync();

            var xml = await DocumentXmlAsync((await Generator(db).GenerateAsync(document.Id, "docx", default))!);

            // The programme's headings, in the programme's order — verification before the records, which is
            // the opposite of what the built-in layout produces.
            Assert.Contains("Section 1 - Verification Basis", xml);
            Assert.Contains("Section 2 - Requirements", xml);
            Assert.True(xml.IndexOf("Section 1 - Verification Basis", StringComparison.Ordinal)
                        < xml.IndexOf("Section 2 - Requirements", StringComparison.Ordinal));
            Assert.DoesNotContain("Annex A - Upward Requirement Traceability", xml);

            // Patterns are filled from the document's own context.
            Assert.Contains("Aircraft-Level Requirements", xml);
            // And the front matter names which layout produced it, so a reader can tell what they are holding.
            Assert.Contains("Aircraft-level SYSRD layout", xml);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_document_keeps_the_layout_it_was_generated_with_after_the_template_is_revised()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var first = await ApproveAsync(db, seed.ProjectId, Layout);
            var document = new ControlledDocument(seed.ProjectId, seed.ReleaseId, seed.BaselineId,
                ControlledDocumentType.Sysrd, "SYSRD-000901", "Fallback title", 0, new string('c', 64), 12,
                DateTimeOffset.UtcNow, first.Id);
            db.ControlledDocuments.Add(document);
            await db.SaveChangesAsync();

            // The programme revises its standard. The prior approved revision stays exactly as it was.
            var template = await db.DocumentTemplates.SingleAsync(x => x.Id == first.TemplateId);
            template.BeginSuccessorRevision("config.manager", DateTimeOffset.UtcNow);
            var revisedBody = Layout.Replace("Section 2 - Requirements", "Section 2 - Renamed Requirements");
            template.UpdateDraft(template.Title, revisedBody, "config.manager", DateTimeOffset.UtcNow);
            var number = template.Approve("config.manager", DateTimeOffset.UtcNow);
            db.DocumentTemplateRevisions.Add(new DocumentTemplateRevision(template.Id, number, "Sysrd",
                "ACME Aerospace", revisedBody, new string('d', 64), "config.manager", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            // Regenerating must produce the document that was approved. Otherwise revising a template
            // silently changes every document generated before it, and the recorded hash proves nothing.
            var xml = await DocumentXmlAsync((await Generator(db).GenerateAsync(document.Id, "docx", default))!);
            Assert.Contains("Section 2 - Requirements", xml);
            Assert.DoesNotContain("Renamed", xml);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_project_with_no_layout_generates_exactly_as_before()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var document = await db.ControlledDocuments.AsNoTracking()
                .FirstAsync(x => x.BaselineId == seed.BaselineId && x.Type == ControlledDocumentType.Sysrd);

            // Introducing templates must not change what a programme that has not adopted one produces.
            var xml = await DocumentXmlAsync((await Generator(db).GenerateAsync(document.Id, "docx", default))!);
            Assert.Contains("Controlled Records", xml);
            Assert.Contains("Annex A - Upward Requirement Traceability", xml);
            Assert.Contains("Built-in layout", xml);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_template_body_that_is_not_a_layout_falls_back_rather_than_failing()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            // Template bodies predate this schema. One that is not a layout is legitimate stored content,
            // and refusing to generate would be a defect in the generator rather than in the record.
            var revision = await ApproveAsync(db, seed.ProjectId, """{"organization":"ACME","notes":"free form"}""");
            var document = new ControlledDocument(seed.ProjectId, seed.ReleaseId, seed.BaselineId,
                ControlledDocumentType.Sysrd, "SYSRD-000902", "Fallback title", 0, new string('e', 64), 12,
                DateTimeOffset.UtcNow, revision.Id);
            db.ControlledDocuments.Add(document);
            await db.SaveChangesAsync();

            var xml = await DocumentXmlAsync((await Generator(db).GenerateAsync(document.Id, "docx", default))!);
            Assert.Contains("Controlled Records", xml);
            Assert.Contains("Fallback title", xml);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_procedure_document_keeps_its_exact_manifest_records_after_unrelated_activity()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var document = await db.ControlledDocuments.AsNoTracking()
                .FirstAsync(x => x.BaselineId == seed.BaselineId
                                 && x.Type == ControlledDocumentType.SystemTestProcedures);
            var before = await DocumentXmlAsync((await Generator(db).GenerateAsync(document.Id, "docx", default))!);

            // Unrelated later activity: an approved procedure revision that is NOT part of the released
            // baseline's exact manifest must not appear in, or change, the already-created document.
            var now = DateTimeOffset.UtcNow;
            var unrelated = new TestProcedure(seed.ProjectId, "SYSTP-099999", "Uncarried later procedure",
                "verification.engineer", now, TestProcedureLevel.System);
            db.TestProcedures.Add(unrelated);
            db.TestProcedureRevisions.Add(new TestProcedureRevision(unrelated.Id, 0, "Later objective",
                "Preconditions", "Steps", "Expected", TestProcedureState.Approved, "verification.engineer", now,
                effectiveBaselineId: seed.BaselineId));
            await db.SaveChangesAsync();

            var after = await DocumentXmlAsync((await Generator(db).GenerateAsync(document.Id, "docx", default))!);
            Assert.Equal(before, after);
            Assert.DoesNotContain("SYSTP-099999", after);
        }
        finally { File.Delete(seed.Path); }
    }
}
