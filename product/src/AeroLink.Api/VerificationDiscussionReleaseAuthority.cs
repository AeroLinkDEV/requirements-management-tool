using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Discussion writes are build-scoped mutations.  The release and exact revision are therefore checked in one
/// place before either a comment or a notification is added.  Reads remain available for historical builds.
/// </summary>
internal static class VerificationDiscussionReleaseAuthority
{
    internal sealed record Decision(string? Code, string? Error)
    {
        public bool Allowed => Code is null;
    }

    internal static async Task<Decision> ValidateAsync(AeroLinkDbContext db, Guid projectId,
        Guid? releaseId, Guid? revisionId, Guid procedureId, CancellationToken ct)
    {
        if (releaseId is null)
            return new("release_context_required", "A release context is required for verification discussion mutations.");

        var release = await db.Releases.AsNoTracking()
            .Where(x => x.Id == releaseId.Value)
            .Select(x => new { x.ProjectId, x.IsReleased })
            .SingleOrDefaultAsync(ct);
        if (release is null)
            return new("release_not_found", "The selected release does not exist.");
        if (release.ProjectId != projectId)
            return new("release_project_mismatch", "The selected release does not belong to this verification artifact's project.");
        if (release.IsReleased)
            return new("released_build_read_only", "Discussion cannot be changed on a released build.");
        if (revisionId is null)
            return new("release_revision_required", "An exact effective revision is required for verification discussion mutations.");

        var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, projectId, releaseId.Value, ct);
        if (effectivity is null || !effectivity.RevisionByProcedure.TryGetValue(procedureId, out var effectiveRevisionId))
            return new("release_artifact_not_effective", "This verification artifact is not effective for the selected release.");
        if (effectiveRevisionId != revisionId.Value)
            return new("release_revision_mismatch", "The requested revision is not the exact revision effective for the selected release.");

        return new(null, null);
    }
}
