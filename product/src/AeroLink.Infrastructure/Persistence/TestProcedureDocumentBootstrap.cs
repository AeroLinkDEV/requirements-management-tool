using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Gives every Project its three test procedure documents, and puts every procedure in one.
///
/// Procedures had no container: a requirement is authored into SYSRD, HLRD or LLRD and its place in that
/// document is part of what it is, while a procedure belonged only to a project and a level. This creates
/// the missing counterparts — SYSTD, HLRTD, LLRTD — and places the procedures that already exist.
///
/// Idempotent and additive. It creates what is absent and never moves a procedure somebody has already
/// filed: a document arranged by an engineer is a structure they decided, and re-running startup must not
/// quietly rearrange it. That also makes it safe to run on every start rather than once behind a flag.
///
/// The placement it does make is deliberately dull — one section per document, everything in it — because
/// inventing a section structure nobody chose would be a worse answer than an obvious flat one somebody can
/// restructure later.
/// </summary>
public sealed class TestProcedureDocumentBootstrap(AeroLinkDbContext db)
{
    /// <summary>The section every backfilled procedure lands in, named so its provenance is obvious.</summary>
    public const string DefaultSectionHeading = "Unsectioned procedures";

    private static readonly (TestProcedureLevel Level, string Acronym, string Title)[] Documents =
    [
        (TestProcedureLevel.System, "SYSTD", "System Test Procedures Document"),
        (TestProcedureLevel.HighLevel, "HLRTD", "High-Level Software Test Procedures Document"),
        (TestProcedureLevel.LowLevel, "LLRTD", "Low-Level Software Test Procedures Document"),
    ];

    public async Task EnsureAllAsync(CancellationToken ct = default)
    {
        var projectIds = await db.Projects.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        foreach (var projectId in projectIds) await EnsureForProjectAsync(projectId, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task EnsureForProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await db.TestProcedureDocuments.Where(x => x.ProjectId == projectId).ToListAsync(ct);

        foreach (var (level, acronym, title) in Documents)
        {
            var document = existing.FirstOrDefault(x => x.Level == level);
            if (document is null)
            {
                // Numbered across the installation rather than within the project — see NextNumberAsync. The
                // number is the document's name, so a project's document is not necessarily SYSTD-000001.
                document = new TestProcedureDocument(projectId, $"{acronym}-{await NextNumberAsync(acronym, ct):D6}",
                    title, level, $"Controlled {title.ToLowerInvariant()} for this project.", "system.bootstrap", now);
                db.TestProcedureDocuments.Add(document);
                existing.Add(document);
            }

            var section = await FindOrCreateDefaultSectionAsync(document, now, ct);
            await PlaceUnfiledProceduresAsync(projectId, level, document, section, now, ct);
        }
    }

    /// <summary>
    /// The next free number for this acronym. Read across projects because the number is the document's
    /// name, and two documents answering to SYSTD-000001 would make a reference ambiguous.
    /// </summary>
    private async Task<int> NextNumberAsync(string acronym, CancellationToken ct)
    {
        var prefix = $"{acronym}-";
        var used = await db.TestProcedureDocuments.AsNoTracking()
            .Where(x => x.DocumentNumber.StartsWith(prefix))
            .Select(x => x.DocumentNumber)
            .ToListAsync(ct);
        var pending = db.TestProcedureDocuments.Local
            .Where(x => x.DocumentNumber.StartsWith(prefix))
            .Select(x => x.DocumentNumber);
        var highest = used.Concat(pending)
            .Select(x => int.TryParse(x[prefix.Length..], out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();
        return highest + 1;
    }

    private async Task<TestProcedureDocumentNode> FindOrCreateDefaultSectionAsync(TestProcedureDocument document,
        DateTimeOffset now, CancellationToken ct)
    {
        var sections = await db.TestProcedureDocumentNodes
            .Where(x => x.DocumentId == document.Id && x.Type == TestProcedureDocumentNodeType.Section)
            .ToListAsync(ct);
        var pendingSections = db.TestProcedureDocumentNodes.Local
            .Where(x => x.DocumentId == document.Id && x.Type == TestProcedureDocumentNodeType.Section);
        var existing = sections.Concat(pendingSections)
            .FirstOrDefault(x => x.Heading == DefaultSectionHeading);
        if (existing is not null) return existing;

        var section = new TestProcedureDocumentNode(document.Id, null, 0,
            TestProcedureDocumentNodeType.Section, DefaultSectionHeading, null, "system.bootstrap", now);
        db.TestProcedureDocumentNodes.Add(section);
        return section;
    }

    private async Task PlaceUnfiledProceduresAsync(Guid projectId, TestProcedureLevel level,
        TestProcedureDocument document, TestProcedureDocumentNode section, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await db.TestProcedures.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.Level == level)
            .OrderBy(x => x.BaseNumber)
            .Select(x => x.Id)
            .ToListAsync(ct);
        // Restricted to this level's procedures rather than reading every filed node in the installation.
        // This runs on every materialisation now, not once at boot, so what it costs is what a test change
        // request costs to approve.
        var filedSet = (await db.TestProcedureDocumentNodes.AsNoTracking()
            .Where(x => x.ProcedureId != null && candidates.Contains(x.ProcedureId!.Value))
            .Select(x => x.ProcedureId!.Value)
            .ToListAsync(ct)).ToHashSet();

        // Positions continue after whatever is already in the section, so a re-run appends rather than
        // colliding with the unique (document, parent, position) index.
        var taken = await db.TestProcedureDocumentNodes.AsNoTracking()
            .Where(x => x.DocumentId == document.Id && x.ParentId == section.Id)
            .Select(x => x.Position)
            .ToListAsync(ct);
        var next = taken.DefaultIfEmpty(-1).Max() + 1;

        foreach (var procedureId in candidates)
        {
            if (filedSet.Contains(procedureId)) continue;
            db.TestProcedureDocumentNodes.Add(new TestProcedureDocumentNode(document.Id, section.Id, next++,
                TestProcedureDocumentNodeType.Procedure, "", procedureId, "system.bootstrap", now));
        }
    }
}
