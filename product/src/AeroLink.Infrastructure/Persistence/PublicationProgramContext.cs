using AeroLink.Domain.Programs;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Keep isolated setup backing identities out of user-facing publication labels.</summary>
internal static class PublicationProgramContext
{
    public static async Task<string> ResolveAsync(AeroLinkDbContext db, ProjectRecord project, ProgramRecord program,
        CancellationToken ct) => await db.ProjectSetupDrafts.AsNoTracking().AnyAsync(draft =>
            draft.ProjectId == project.Id && draft.InternalProgramId == program.Id
            && draft.State == ProjectSetupState.Completed, ct)
        ? "" : $"{program.Name} ({program.Code})";
}
