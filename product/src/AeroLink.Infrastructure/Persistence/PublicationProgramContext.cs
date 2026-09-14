using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Keep isolated setup backing identities out of user-facing publication labels.</summary>
internal static class PublicationProgramContext
{
    /// <summary>
    /// Returns the release label that belongs in a publication's user-facing build fields.
    ///
    /// Setup projects deliberately keep the raw operator-entered version on the release for historical
    /// evidence, while their new controlled publications use the official build identity.  The completed
    /// setup-draft link is the existing boundary that distinguishes those projects from legacy/program-backed
    /// records.  Keeping the choice here gives every publication generator one policy and leaves controlled
    /// numbering (which still receives the raw version) untouched.
    /// </summary>
    public static async Task<string> ResolveReleaseLabelAsync(AeroLinkDbContext db, ProjectRecord project,
        ProgramRecord program, SoftwareRelease release, CancellationToken ct)
    {
        var isCompletedSetupProject = await db.ProjectSetupDrafts.AsNoTracking().AnyAsync(draft =>
            draft.ProjectId == project.Id && draft.InternalProgramId == program.Id
            && draft.State == ProjectSetupState.Completed, ct);

        if (!isCompletedSetupProject)
            return release.Version;

        // New setup releases are assigned CanonicalIdentity by SoftwareRelease. The fallback keeps output
        // truthful for an older completed setup row that predates that persisted field without changing its
        // raw Version value.
        return release.CanonicalIdentity ?? SoftwareBuildIdentifier.FromVersion(release.Version);
    }

    public static async Task<string> ResolveAsync(AeroLinkDbContext db, ProjectRecord project, ProgramRecord program,
        CancellationToken ct) => await db.ProjectSetupDrafts.AsNoTracking().AnyAsync(draft =>
            draft.ProjectId == project.Id && draft.InternalProgramId == program.Id
            && draft.State == ProjectSetupState.Completed, ct)
        ? "" : $"{program.Name} ({program.Code})";
}
