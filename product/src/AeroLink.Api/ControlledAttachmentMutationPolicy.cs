using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>One failed controlled-attachment authorization, in the shape endpoints map to HTTP.</summary>
public readonly record struct ControlledAttachmentMutationFailure(int Status, string Error, string? Code)
{
    public IResult ToResult() => Status switch
    {
        StatusCodes.Status403Forbidden => Results.Forbid(),
        StatusCodes.Status409Conflict => Results.Conflict(new { error = Error, code = Code }),
        _ => Results.BadRequest(new { error = Error, code = Code }),
    };
}

/// <summary>
/// The one server-side authorization and exact-revision binding policy for controlled attachment
/// mutations. Program membership alone reads controlled evidence; it must never be the thing that
/// authorizes replacing it, and exported evidence must carry the exact revision it attests.
///
/// Each supported artifact type keeps its own authoritative capability, and the policy is where they are
/// named: Requirement evidence demands current engineering mutation authority plus an exact, eligible,
/// unreleased revision; Change Request evidence keeps the governed Draft-author/administrator rule;
/// Problem Report evidence stays gated by the exclusive edit-session checkout capability, which the
/// upload endpoint enforces through the session and aggregate locks it already owns.
///
/// Unknown artifact types fail closed. Supersession never jumps artifact type, artifact identity, or the
/// revision identity a bound chain already carries; a legacy chain with no revision binding may be bound
/// only by explicitly supplying an eligible in-work revision — history is never backfilled by inference.
/// </summary>
public static class ControlledAttachmentMutationPolicy
{
    public const string UnsupportedArtifactTypeCode = "unsupported_artifact_type";
    public const string RevisionIdentityRequiredCode = "revision_identity_required";
    public const string RevisionIdentityMismatchCode = "revision_identity_mismatch";
    public const string RevisionNotCurrentCode = "revision_not_current";
    public const string RevisionReleasedCode = "revision_released";
    public const string ChainRevisionMismatchCode = "attachment_chain_revision_mismatch";

    /// <summary>
    /// Who may attach to, or supersede evidence on, this artifact — before any request body is parsed.
    /// The caller must have re-resolved <paramref name="artifactType"/> to its canonical spelling so the
    /// authorization decision and the transactional write cannot disagree about the discriminator.
    /// </summary>
    public static async Task<ControlledAttachmentMutationFailure?> AuthorizeArtifactAsync(
        AeroLinkDbContext db, IdentityService identity, HttpContext http,
        Guid projectId, string artifactType, Guid artifactId, CancellationToken ct)
    {
        switch (artifactType)
        {
            case "Requirement":
                if (!await db.Requirements.AsNoTracking().AnyAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct))
                    return new(400, "The controlled artifact does not belong to this Project.", null);
                // Engineering authority is resolved through the project's current positions and standing
                // demands, not through whatever membership happens to grant read access today.
                if (!await http.HasProjectRoleAsync(db, identity, projectId, ct, ProgramRole.Engineer))
                    return new(403, "Requirement supporting evidence requires engineering mutation authority for this Project.", null);
                return null;
            case "ChangeRequest":
                var changeRequest = await db.SystemChangeRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId && x.ProjectId == projectId, ct);
                if (changeRequest is null)
                    return new(400, "The controlled artifact does not belong to this Project.", null);
                var actor = http.UserAccount();
                if (!actor.IsAdministrator && !string.Equals(changeRequest.AuthorId, actor.UserName, StringComparison.OrdinalIgnoreCase))
                    return new(403, "Only the change request's author or an administrator may change its supporting files.", null);
                if (changeRequest.State != ChangeRequestState.Draft)
                    return new(409, "Supporting files can be added only while the change request is a Draft.", "artifact_not_editable");
                return null;
            default:
                return new(400, $"'{artifactType}' is not a supported controlled attachment artifact type.", UnsupportedArtifactTypeCode);
        }
    }

    /// <summary>
    /// The exact-revision binding contract for Requirement evidence: the supplied revision must be real and
    /// belong to this artifact, still be the requirement's current eligible revision, not be carried by a
    /// released baseline, and — when the logical chain already carries a binding — stay within that exact
    /// revision identity. Membership in a released baseline is read from the authoritative
    /// <c>BaselineRequirementSelection</c> records, never from a revision's effective-baseline shortcut.
    /// Run before identifier allocation, storage writes, or any durable state changes.
    /// </summary>
    public static async Task<ControlledAttachmentMutationFailure?> ValidateRequirementRevisionAsync(
        AeroLinkDbContext db, Guid projectId, Guid artifactId, Guid revisionId,
        ControlledAttachment? previousChainHead, CancellationToken ct)
    {
        var revision = await db.RequirementRevisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == revisionId && x.ArtifactId == artifactId, ct);
        if (revision is null)
            return new(400, "The selected revision does not belong to this requirement.", RevisionIdentityMismatchCode);
        var supersededByNewer = await db.RequirementRevisions.AsNoTracking()
            .AnyAsync(x => x.ArtifactId == artifactId && x.Revision > revision.Revision, ct);
        if (revision.State != RequirementRevisionState.Active || supersededByNewer)
            return new(409, "Requirement evidence binds to the requirement's current in-work revision; supply its exact current revision identity.", RevisionNotCurrentCode);
        var released = await (from membership in db.BaselineRequirements.AsNoTracking()
                              join baseline in db.CandidateBaselines.AsNoTracking() on membership.BaselineId equals baseline.Id
                              where membership.RevisionId == revisionId && baseline.ProjectId == projectId
                                    && baseline.State == CandidateBaselineState.Released
                              select membership.Id).AnyAsync(ct);
        if (released)
            return new(409, "This requirement revision is carried by a released baseline; its controlled evidence can no longer be replaced through the general upload endpoint.", RevisionReleasedCode);
        if (previousChainHead is { RevisionId: not null } && previousChainHead.RevisionId != revisionId)
            return new(409, "A superseding attachment stays within its logical chain's exact revision identity; attach evidence for a different revision as a new chain.", ChainRevisionMismatchCode);
        return null;
    }
}
