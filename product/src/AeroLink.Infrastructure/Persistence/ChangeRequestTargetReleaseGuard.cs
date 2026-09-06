using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Why a target release cannot carry a change request.</summary>
public enum ChangeRequestTargetReleaseRejection
{
    /// <summary>
    /// The release does not exist, or it exists in another Project. Both share one posture: distinguishing
    /// them would turn any change-request endpoint into a cross-project release-identifier oracle.
    /// </summary>
    NotFoundOrForeign,

    /// <summary>The release belongs to this Project but has been released and takes no new change requests.</summary>
    Released,
}

/// <summary>The shared decision of one target release against one change-request project.</summary>
public readonly record struct ChangeRequestTargetReleaseVerdict(
    bool Eligible,
    ChangeRequestTargetReleaseRejection? Rejection,
    string ReleasedVersion)
{
    public static ChangeRequestTargetReleaseVerdict Accept() => new(true, null, string.Empty);
}

/// <summary>
/// The one typed guard for every user- or service-supplied change-request target release, at construction
/// and at retargeting. A change request must never persist a target release that is nonexistent, foreign
/// to its project, or no longer eligible because it has been released: the domain stores project and
/// release identities independently, so only this check keeps the pair honest — and it must run before
/// identifier allocation, persistence, events, or any other durable side effect.
///
/// Route every change-request construction path through this guard rather than restating the checks:
/// creation, drafts, import commit, integration and OSLC creation, ReqIF commit, and retargeting.
/// </summary>
public sealed class ChangeRequestTargetReleaseGuard(AeroLinkDbContext db)
{
    /// <summary>The stable code for the shared not-found posture (foreign and nonexistent are indistinguishable).</summary>
    public const string NotFoundCode = "target_release_not_found";

    /// <summary>The stable lifecycle code for a same-project release that can no longer be targeted.</summary>
    public const string ReleasedCode = "release_is_closed";

    public const string NotFoundError = "The target build does not exist in this Project.";

    public async Task<ChangeRequestTargetReleaseVerdict> ValidateAsync(Guid projectId, Guid targetReleaseId, CancellationToken ct)
    {
        var release = await db.Releases.AsNoTracking()
            .Where(x => x.Id == targetReleaseId)
            .Select(x => new { x.ProjectId, x.Version, x.IsReleased })
            .SingleOrDefaultAsync(ct);
        if (release is null || release.ProjectId != projectId)
            return new(false, ChangeRequestTargetReleaseRejection.NotFoundOrForeign, string.Empty);
        return release.IsReleased
            ? new(false, ChangeRequestTargetReleaseRejection.Released, release.Version)
            : ChangeRequestTargetReleaseVerdict.Accept();
    }
}
