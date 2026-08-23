using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Every Project gets its three test procedure documents, and every procedure gets a place in one.
///
/// Procedures had no container: a requirement is authored into SYSRD, HLRD or LLRD and its place in that
/// document is part of what it is, while a procedure belonged only to a project and a level. These assert the
/// counterparts exist, that a procedure lands in the document for its own level, and — the part that matters
/// on a live database — that running it again changes nothing.
/// </summary>
public sealed class TestProcedureDocumentBootstrapTests
{
    private sealed record Fixture(AeroLinkDbContext Db, Guid ProjectId, Guid SystemProcedureId,
        Guid HighLevelProcedureId, Guid LowLevelProcedureId);

    private static async Task<Fixture> DatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;

        var program = new ProgramRecord("Document Program", "DOC");
        var project = new ProjectRecord(program.Id, "Flight Software", "Document Software");
        db.AddRange(program, project);

        var system = new TestProcedure(project.Id, "SYSTP-000001", "Verify oceanic sequencing", "test.engineer", now,
            TestProcedureLevel.System);
        var high = new TestProcedure(project.Id, "HLRTP-000001", "Verify flight plan behaviour", "test.engineer", now,
            TestProcedureLevel.HighLevel);
        var low = new TestProcedure(project.Id, "LLRTP-000001", "Verify checksum recovery", "test.engineer", now,
            TestProcedureLevel.LowLevel);
        db.AddRange(system, high, low);
        await db.SaveChangesAsync();

        return new(db, project.Id, system.Id, high.Id, low.Id);
    }

    [Fact]
    public async Task A_project_gets_one_document_for_each_level()
    {
        var fixture = await DatabaseAsync();
        await new TestProcedureDocumentBootstrap(fixture.Db).EnsureAllAsync();

        var documents = await fixture.Db.TestProcedureDocuments.AsNoTracking()
            .Where(x => x.ProjectId == fixture.ProjectId).OrderBy(x => x.DocumentNumber).ToListAsync();

        Assert.Equal(3, documents.Count);
        // The verification counterparts of SYSRD, HLRD and LLRD, named the way the owner names them.
        Assert.Equal(["HLRTD-000001", "LLRTD-000001", "SYSTD-000001"], documents.Select(x => x.DocumentNumber));
        Assert.Equal([TestProcedureLevel.HighLevel, TestProcedureLevel.LowLevel, TestProcedureLevel.System],
            documents.Select(x => x.Level));
        Assert.Equal("High-Level Software Test Cases Document",
            documents.Single(x => x.Level == TestProcedureLevel.HighLevel).Title);
        Assert.Equal("Low-Level Software Test Cases Document",
            documents.Single(x => x.Level == TestProcedureLevel.LowLevel).Title);
        Assert.Equal("System Test Procedures Document",
            documents.Single(x => x.Level == TestProcedureLevel.System).Title);
    }

    [Fact]
    public async Task A_procedure_is_placed_in_the_document_for_its_own_level()
    {
        var fixture = await DatabaseAsync();
        await new TestProcedureDocumentBootstrap(fixture.Db).EnsureAllAsync();

        var placements = await (from node in fixture.Db.TestProcedureDocumentNodes.AsNoTracking()
                                where node.ProcedureId != null
                                join document in fixture.Db.TestProcedureDocuments.AsNoTracking()
                                    on node.DocumentId equals document.Id
                                select new { node.ProcedureId, document.Level }).ToListAsync();

        Assert.Equal(3, placements.Count);
        Assert.Equal(TestProcedureLevel.System,
            placements.Single(x => x.ProcedureId == fixture.SystemProcedureId).Level);
        Assert.Equal(TestProcedureLevel.HighLevel,
            placements.Single(x => x.ProcedureId == fixture.HighLevelProcedureId).Level);
        Assert.Equal(TestProcedureLevel.LowLevel,
            placements.Single(x => x.ProcedureId == fixture.LowLevelProcedureId).Level);
    }

    [Fact]
    public async Task Every_placed_procedure_sits_inside_a_section()
    {
        var fixture = await DatabaseAsync();
        await new TestProcedureDocumentBootstrap(fixture.Db).EnsureAllAsync();

        var nodes = await fixture.Db.TestProcedureDocumentNodes.AsNoTracking().ToListAsync();
        var documentLevels = await fixture.Db.TestProcedureDocuments.AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.Level);
        var sections = nodes.Where(x => x.Type == TestProcedureDocumentNodeType.Section).ToList();
        var procedures = nodes.Where(x => x.Type == TestProcedureDocumentNodeType.Procedure).ToList();

        Assert.Equal(3, sections.Count);
        Assert.All(sections, x => Assert.Equal(
            documentLevels[x.DocumentId] == TestProcedureLevel.System
                ? TestProcedureDocumentBootstrap.DefaultSectionHeading
                : TestProcedureDocumentBootstrap.DefaultCaseSectionHeading,
            x.Heading));
        // A procedure hangs under a section rather than loose at the document root, which is what makes the
        // structure a document rather than a list.
        Assert.All(procedures, x => Assert.Contains(sections, section => section.Id == x.ParentId));
    }

    /// <summary>
    /// The property that makes it safe to run on every start against a live database.
    /// </summary>
    [Fact]
    public async Task Running_it_again_creates_nothing_and_moves_nothing()
    {
        var fixture = await DatabaseAsync();
        var bootstrap = new TestProcedureDocumentBootstrap(fixture.Db);
        await bootstrap.EnsureAllAsync();

        var documentsBefore = await fixture.Db.TestProcedureDocuments.AsNoTracking()
            .Select(x => new { x.Id, x.DocumentNumber }).OrderBy(x => x.DocumentNumber).ToListAsync();
        var nodesBefore = await fixture.Db.TestProcedureDocumentNodes.AsNoTracking()
            .Select(x => new { x.Id, x.DocumentId, x.ParentId, x.Position, x.ProcedureId })
            .OrderBy(x => x.Id).ToListAsync();

        await bootstrap.EnsureAllAsync();

        var documentsAfter = await fixture.Db.TestProcedureDocuments.AsNoTracking()
            .Select(x => new { x.Id, x.DocumentNumber }).OrderBy(x => x.DocumentNumber).ToListAsync();
        var nodesAfter = await fixture.Db.TestProcedureDocumentNodes.AsNoTracking()
            .Select(x => new { x.Id, x.DocumentId, x.ParentId, x.Position, x.ProcedureId })
            .OrderBy(x => x.Id).ToListAsync();

        Assert.Equal(documentsBefore, documentsAfter);
        Assert.Equal(nodesBefore, nodesAfter);
    }

    /// <summary>
    /// A procedure somebody has already filed stays where they filed it. Re-running startup must never
    /// quietly rearrange a document an engineer arranged.
    /// </summary>
    [Fact]
    public async Task A_procedure_already_filed_elsewhere_is_left_alone()
    {
        var fixture = await DatabaseAsync();
        var bootstrap = new TestProcedureDocumentBootstrap(fixture.Db);
        await bootstrap.EnsureAllAsync();

        var systemDocument = await fixture.Db.TestProcedureDocuments
            .SingleAsync(x => x.Level == TestProcedureLevel.System);
        var chosen = new TestProcedureDocumentNode(systemDocument.Id, null, 99,
            TestProcedureDocumentNodeType.Section, "Oceanic sequencing", null, "test.engineer", DateTimeOffset.UtcNow);
        fixture.Db.TestProcedureDocumentNodes.Add(chosen);
        var placement = await fixture.Db.TestProcedureDocumentNodes
            .SingleAsync(x => x.ProcedureId == fixture.SystemProcedureId);
        placement.UpdateDraft(chosen.Id, 0, "", DateTimeOffset.UtcNow);
        await fixture.Db.SaveChangesAsync();

        await bootstrap.EnsureAllAsync();

        var after = await fixture.Db.TestProcedureDocumentNodes.AsNoTracking()
            .SingleAsync(x => x.ProcedureId == fixture.SystemProcedureId);
        Assert.Equal(chosen.Id, after.ParentId);
    }

    /// <summary>A second Project gets its own documents, numbered in its own right.</summary>
    [Fact]
    public async Task A_second_project_gets_its_own_documents()
    {
        var fixture = await DatabaseAsync();
        var program = new ProgramRecord("Second Program", "SEC");
        var second = new ProjectRecord(program.Id, "Flight Software", "Second Software");
        fixture.Db.AddRange(program, second);
        await fixture.Db.SaveChangesAsync();

        await new TestProcedureDocumentBootstrap(fixture.Db).EnsureAllAsync();

        var theirs = await fixture.Db.TestProcedureDocuments.AsNoTracking()
            .Where(x => x.ProjectId == second.Id).ToListAsync();
        Assert.Equal(3, theirs.Count);
        // Numbered uniquely across projects, because the number is the document's name and two documents
        // answering to SYSTD-000001 would make a reference ambiguous.
        var all = await fixture.Db.TestProcedureDocuments.AsNoTracking().Select(x => x.DocumentNumber).ToListAsync();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
