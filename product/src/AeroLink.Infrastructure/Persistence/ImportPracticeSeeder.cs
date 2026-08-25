using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// A Program that exists to be practised on.
///
/// Bringing a program in from another tool is rehearsed before it is done for real: import the extract, find
/// the mapping wrong, abandon, re-extract, try again. None of that belongs in a Program somebody is working
/// in, so it gets its own — deliberately empty, with no builds, no requirements and no change requests.
///
/// It is its own Program rather than a second Project inside the showcase one because several showcase steps
/// resolve their Project with <c>SingleAsync</c> over the Program, and a Program that already exists
/// elsewhere is not part of the Flight Management System in any case.
///
/// Rehearsals are disposable. An accepted import is immutable by design and its build cannot be removed, so
/// each rehearsal is named as its own build — 1.0, then 2.0 — and the real import, when it happens, is run
/// into the Project that will actually keep it rather than into this one. "This program was imported into
/// DOORS Import Practice" is not a sentence that should survive in a provenance record.
/// </summary>
public sealed class ImportPracticeSeeder(AeroLinkDbContext db)
{
    public const string ProgramCode = "IMPORTLAB";
    public const string ProjectName = "DOORS Import Practice";

    public async Task<Guid> EnsureSeededAsync(CancellationToken ct = default)
    {
        var existing = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == ProgramCode, ct);
        if (existing is not null)
            return await db.Projects.AsNoTracking().Where(x => x.ProgramId == existing.Id)
                .Select(x => x.Id).SingleAsync(ct);

        var program = new ProgramRecord(ProjectName, ProgramCode);
        var project = new ProjectRecord(program.Id, ProjectName, "Imported baseline rehearsal");
        var ladder = LegacyDefaultProjectLadderFactory.Create(project.Id, DateTimeOffset.UtcNow);
        db.AddRange(program, project, ladder, ProjectVerificationVocabulary.Founding(project.Id, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
        return project.Id;
    }
}
