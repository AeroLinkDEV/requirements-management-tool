using AeroLink.Domain.Common;

namespace AeroLink.Domain.ChangeControl;

/// <summary>
/// Which change request currently holds the right to change a requirement or a procedure.
///
/// Two change requests may both be written against the same requirement — that is legitimate, and often
/// deliberate — but only one may be in front of reviewers at a time. Without that, the approved wording of a
/// controlled requirement depends on merge order rather than on anybody's intent, and the loser's analysis is
/// discarded without either author being told.
///
/// The claim is deliberately a row rather than a query over change request state, and the reason is the race
/// it exists to settle. "First to submit wins" cannot be decided by reading state and then writing it: two
/// submissions issued at the same moment both read no holder and both proceed. A unique index over the
/// artifact lets the database pick the winner, in the same transaction that moves the change request into
/// review, and the loser is refused rather than silently accepted.
///
/// The row is derived, not authoritative. A change request's state remains the truth of whether it holds a
/// claim; this table exists so that truth can be established atomically. Stale rows are therefore expected —
/// see <see cref="ArtifactClaimKey"/> and the release path — and are cleared before a claim is taken rather
/// than swept on a timer.
/// </summary>
public sealed class ArtifactClaim
{
    private ArtifactClaim() { }

    public ArtifactClaim(Guid projectId, string artifactKey, Guid changeRequestId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(artifactKey)) throw new DomainException("A claim needs the artifact it is over.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        ArtifactKey = artifactKey.Trim();
        ChangeRequestId = changeRequestId;
        AcquiredAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }

    /// <summary>
    /// The artifact this claim is over, as one string so requirements and procedures share a single unique
    /// index. Built by <see cref="ArtifactClaimKey"/> — never composed by hand at a call site, because two
    /// spellings of the same requirement would be two claims and the contention would go undetected.
    /// </summary>
    public string ArtifactKey { get; private set; } = string.Empty;

    public Guid ChangeRequestId { get; private set; }
    public DateTimeOffset AcquiredAt { get; private set; }
}

/// <summary>
/// The one place an artifact's claim key is spelled.
/// </summary>
public static class ArtifactClaimKey
{
    /// <summary>
    /// A requirement is identified by its base number, not by the revision being changed: two change requests
    /// modifying different revisions of the same requirement are contending, which is the whole point.
    /// </summary>
    public static string ForRequirement(string baseNumber) => $"requirement:{Normalize(baseNumber)}";

    public static string ForProcedure(string baseNumber) => $"procedure:{Normalize(baseNumber)}";

    private static string Normalize(string baseNumber)
    {
        if (string.IsNullOrWhiteSpace(baseNumber)) throw new DomainException("A claim needs the artifact it is over.");
        return baseNumber.Trim().ToUpperInvariant();
    }
}

/// <summary>
/// The states in which a change request holds its claims.
///
/// Draft holds nothing: an author may write against a requirement somebody else is having reviewed, and is
/// warned rather than stopped. Deferred holds nothing either — shelved work must not block the build it was
/// shelved out of, and a reinstated change request comes back as a Draft and re-enters the queue behind
/// whoever submitted while it was away.
/// </summary>
public static class ClaimHolding
{
    public static bool Holds(ChangeRequestState state) => state
        is ChangeRequestState.InReview
        or ChangeRequestState.Approved
        or ChangeRequestState.SelectedForBaseline;
}
