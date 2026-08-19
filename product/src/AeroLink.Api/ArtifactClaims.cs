using AeroLink.Domain.ChangeControl;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Who currently holds the right to change a requirement, and what happens when two change requests want it.
///
/// Contention is legitimate. Two authors may write against the same requirement, and neither is stopped from
/// doing so — they are told. What cannot happen is two packages proposing different wording for the same
/// controlled requirement reaching reviewers at once, because then the approved text depends on the order the
/// approvals happened to land in.
///
/// So the rule is: warn while authoring, decide at submission. The change request that submits first takes
/// the claim; the second is refused and told what to do about it.
/// </summary>
public static class ArtifactClaims
{
    /// <summary>
    /// What a change request contends on. Introduce is excluded: it creates a requirement that does not exist
    /// yet, so there is nothing for a second change request to be holding.
    /// </summary>
    public static IReadOnlyList<string> KeysFor(SystemChangeRequest scr) => scr.RequirementChanges
        .Where(x => x.Kind is RequirementChangeKind.Modify or RequirementChangeKind.Retire)
        .Select(x => ArtifactClaimKey.ForRequirement(x.BaseNumber))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public sealed record Contention(string ArtifactKey, string BaseNumber, Guid ChangeRequestId,
        string DisplayNumber, ChangeRequestState State, bool Holds);

    /// <summary>
    /// Every other change request writing against the same requirements, whether or not it holds a claim.
    ///
    /// Reads change request state rather than the claim table, because the answer must include change
    /// requests that hold nothing — a Draft, or a deferred record — so the author can be told about them.
    /// The claim table settles the race; this explains it.
    /// </summary>
    public static async Task<IReadOnlyList<Contention>> ContendersAsync(AeroLinkDbContext db, Guid projectId,
        IReadOnlyCollection<string> baseNumbers, Guid excluding, CancellationToken ct)
    {
        if (baseNumbers.Count == 0) return [];
        var upper = baseNumbers.Select(x => x.Trim().ToUpperInvariant()).ToList();
        // Joined from the change table rather than navigated from the change request. Walking the collection
        // navigation translates to APPLY, which SQLite does not support, so the query would work against
        // PostgreSQL and fail in every test.
        var rows = await (from change in db.RequirementChanges.AsNoTracking()
                          join scr in db.SystemChangeRequests.AsNoTracking()
                              on change.ChangeRequestId equals scr.Id
                          where scr.ProjectId == projectId && scr.Id != excluding
                                && (change.Kind == RequirementChangeKind.Modify || change.Kind == RequirementChangeKind.Retire)
                          select new { scr.Id, scr.BaseNumber, scr.Revision, scr.State, Requirement = change.BaseNumber })
            .ToListAsync(ct);

        return rows
            .Where(x => upper.Contains(x.Requirement.Trim().ToUpperInvariant()))
            .Select(x => new Contention(
                ArtifactClaimKey.ForRequirement(x.Requirement), x.Requirement, x.Id,
                $"{x.BaseNumber}.{x.Revision:D2}", x.State, ClaimHolding.Holds(x.State)))
            .DistinctBy(x => (x.ChangeRequestId, x.ArtifactKey))
            .ToList();
    }

    /// <summary>
    /// What an author is told when they write against a requirement somebody else is also changing.
    ///
    /// Three different situations, three different sentences, because "this is contended" is not actionable.
    /// A holder means this change request cannot go to review yet; another draft means it is still a race;
    /// a deferred record means nothing is in the way at all and saying so prevents a needless detour.
    ///
    /// None of these fail the request. The author is writing, not submitting, and being stopped from
    /// recording an analysis because somebody else got there first is not a rule this tool should have.
    /// </summary>
    public static object Notice(Contention contention) => new
    {
        changeRequestId = contention.ChangeRequestId,
        displayNumber = contention.DisplayNumber,
        baseNumber = contention.BaseNumber,
        state = contention.State.ToString(),
        blocking = contention.Holds,
        message = contention.State == ChangeRequestState.Deferred
            ? $"{contention.DisplayNumber} also changes {contention.BaseNumber}, but it is deferred, so you may proceed. If it is reinstated it comes back as a draft, behind you."
            : contention.Holds
                ? $"{contention.DisplayNumber} is {contention.State} for {contention.BaseNumber}. You may keep writing, but this change request cannot go to review until that one releases it."
                : $"{contention.DisplayNumber} is also drafting a change to {contention.BaseNumber}. Whichever is submitted first takes it.",
    };

    /// <summary>Every notice for the requirements a change request currently touches.</summary>
    public static async Task<IReadOnlyList<object>> NoticesAsync(AeroLinkDbContext db, SystemChangeRequest scr,
        CancellationToken ct)
    {
        var numbers = scr.RequirementChanges
            .Where(x => x.Kind is RequirementChangeKind.Modify or RequirementChangeKind.Retire)
            .Select(x => x.BaseNumber).Distinct().ToList();
        if (numbers.Count == 0) return [];
        var contenders = await ContendersAsync(db, scr.ProjectId, numbers, scr.Id, ct);
        return contenders.Select(Notice).ToList();
    }

    /// <summary>
    /// The refusal a losing submission gets. Names the change request in the way and every requirement it is
    /// in the way of, because "somebody else has this" with no way to find out who is a dead end.
    /// </summary>
    public static string Refusal(IReadOnlyList<Contention> blocking)
    {
        var byRequest = blocking.GroupBy(x => (x.DisplayNumber, x.State))
            .Select(g => $"{g.Key.DisplayNumber} ({g.Key.State}) on {string.Join(", ", g.Select(x => x.BaseNumber).Order())}")
            .ToList();
        // Deliberately does not offer to remove the contested requirement or to rebase onto the approved
        // result. Neither exists yet -- there is no way to take a requirement change off a change request --
        // and a refusal that names a remedy the reader cannot carry out is worse than one that does not.
        return "This change request cannot go to review while another is being reviewed or approved for the same "
            + $"requirements: {string.Join("; ", byRequest)}. It can go to review once that change request is "
            + "returned to draft, deferred, or released with its build.";
    }
}
