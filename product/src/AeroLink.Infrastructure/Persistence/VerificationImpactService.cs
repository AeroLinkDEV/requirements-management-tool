using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Raises verification work when an approved change alters what must be tested, and keeps that work
/// attached to the release that will carry the change.
///
/// Items are raised at change-request approval rather than at baseline inclusion so verification can start
/// as soon as the engineering decision is settled, rather than discovering the work when the release is
/// already being assembled.
/// </summary>
public sealed class VerificationImpactService(AeroLinkDbContext db)
{
    /// <summary>
    /// Raises the items owed by a newly approved change request. Safe to call more than once for the same
    /// change request: existing items for the same requirement change are left alone, so a retried approval
    /// never duplicates work.
    /// </summary>
    public async Task<int> RaiseForApprovedChangeRequestAsync(SystemChangeRequest request, DateTimeOffset now, CancellationToken ct)
    {
        // Selecting an approved change into a candidate baseline moves it to SelectedForBaseline, so both
        // states mean "approved". Testing only for Approved would make a retried raise silently do nothing
        // once the change had been selected.
        if (request.State is not (ScrState.Approved or ScrState.SelectedForBaseline)) return 0;

        var alreadyRaised = await db.VerificationImpactItems
            .Where(x => x.ChangeRequestId == request.Id)
            .Select(x => x.RequirementChangeId)
            .ToListAsync(ct);
        var covered = alreadyRaised.Where(x => x is not null).Select(x => x!.Value).ToHashSet();

        var raised = 0;
        foreach (var change in request.RequirementChanges)
        {
            if (covered.Contains(change.Id)) continue;
            var display = ArtifactNumberDisplay(change);
            VerificationImpactItem? item = change.Kind switch
            {
                RequirementChangeKind.Introduce => VerificationImpactItem.ForIntroducedRequirement(
                    request.ProjectId, request.TargetReleaseId, request.Id, change.Id, display, change.VerificationMethod, now),
                RequirementChangeKind.Modify => VerificationImpactItem.ForModifiedRequirement(
                    request.ProjectId, request.TargetReleaseId, request.Id, change.Id, display, change.VerificationMethod, now),
                // Retirement raises work only where it strands a procedure, which is resolved separately
                // once the retirement is materialised and remaining links are known.
                _ => null
            };
            if (item is null) continue;
            db.VerificationImpactItems.Add(item);
            raised++;
        }

        return raised;
    }

    /// <summary>
    /// Moves outstanding verification work with its change request when the target release changes, so the
    /// work is never stranded against a release the change no longer belongs to.
    /// </summary>
    public async Task<int> RetargetAsync(Guid changeRequestId, Guid releaseId, DateTimeOffset now, CancellationToken ct)
    {
        var items = await db.VerificationImpactItems.Where(x => x.ChangeRequestId == changeRequestId).ToListAsync(ct);
        foreach (var item in items) item.Retarget(releaseId, now);
        return items.Count;
    }

    /// <summary>
    /// Raises an item for every procedure left covering no requirement after a retirement was materialised.
    /// A procedure that still covers something else stays quiet.
    /// </summary>
    public async Task<int> RaiseOrphanedProceduresAsync(Guid projectId, Guid releaseId, Guid changeRequestId,
        DateTimeOffset now, CancellationToken ct)
    {
        var linkedProcedureRevisions = await db.TestCoverage.AsNoTracking()
            .Select(x => x.ProcedureRevisionId).Distinct().ToListAsync(ct);

        var orphanedProcedures = await db.TestProcedureRevisions.AsNoTracking()
            .Where(revision => !linkedProcedureRevisions.Contains(revision.Id))
            .Join(db.TestProcedures.AsNoTracking().Where(p => p.ProjectId == projectId),
                revision => revision.ProcedureId, procedure => procedure.Id,
                (revision, procedure) => new { procedure.Id, procedure.BaseNumber })
            .Distinct()
            .ToListAsync(ct);
        if (orphanedProcedures.Count == 0) return 0;

        var alreadyRaised = await db.VerificationImpactItems
            .Where(x => x.Trigger == VerificationImpactTrigger.ProcedureOrphaned && x.State != VerificationImpactState.Resolved)
            .Select(x => x.ProcedureId).ToListAsync(ct);
        var covered = alreadyRaised.Where(x => x is not null).Select(x => x!.Value).ToHashSet();

        var raised = 0;
        foreach (var procedure in orphanedProcedures)
        {
            if (covered.Contains(procedure.Id)) continue;
            db.VerificationImpactItems.Add(VerificationImpactItem.ForOrphanedProcedure(
                projectId, releaseId, changeRequestId, procedure.Id, procedure.BaseNumber, now));
            raised++;
        }
        return raised;
    }

    /// <summary>Unresolved items are what hold a baseline back from approval.</summary>
    public Task<List<VerificationImpactItem>> OutstandingForReleaseAsync(Guid releaseId, CancellationToken ct)
        => db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.ReleaseId == releaseId && x.State != VerificationImpactState.Resolved)
            .OrderBy(x => x.Trigger).ThenBy(x => x.SubjectDisplayNumber)
            .ToListAsync(ct);

    public Task<List<VerificationImpactItem>> ForReleaseAsync(Guid releaseId, CancellationToken ct)
        => db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.ReleaseId == releaseId)
            .OrderBy(x => x.State).ThenBy(x => x.Trigger).ThenBy(x => x.SubjectDisplayNumber)
            .ToListAsync(ct);

    /// <summary>
    /// The release-approval gate. Nobody may authorize a release while a decision is still owed about how one
    /// of its new or changed requirements will be verified. This is enforced as the verification_impact
    /// readiness gate in <see cref="ReleaseReadinessService"/>, so the blockers are visible in the release
    /// workbench rather than surfacing only as a refusal at the final step; this method is the direct check
    /// for callers that need to fail closed without computing the whole readiness picture.
    /// </summary>
    public async Task EnsureReleaseMayBeApprovedAsync(Guid releaseId, CancellationToken ct)
    {
        var outstanding = await OutstandingForReleaseAsync(releaseId, ct);
        if (outstanding.Count == 0) return;
        var subjects = string.Join(", ", outstanding.Take(5).Select(x => x.SubjectDisplayNumber));
        var more = outstanding.Count > 5 ? $" and {outstanding.Count - 5} more" : "";
        throw new DomainException(
            $"{outstanding.Count} verification impact item(s) are unresolved for this release ({subjects}{more}). " +
            "Every new or modified requirement needs an approved procedure or a recorded decision that no test is required before the release can be approved.");
    }

    /// <summary>
    /// Confirms the named procedure exists in the Project and has an approved revision. Coverage may only be
    /// claimed against a procedure that is actually approved.
    /// </summary>
    public async Task<bool> HasApprovedProcedureAsync(Guid projectId, Guid procedureId, CancellationToken ct)
        => await db.TestProcedureRevisions.AsNoTracking()
            .Where(revision => revision.ProcedureId == procedureId && revision.State == TestProcedureState.Approved)
            .Join(db.TestProcedures.AsNoTracking().Where(p => p.ProjectId == projectId),
                revision => revision.ProcedureId, procedure => procedure.Id, (revision, _) => revision.Id)
            .AnyAsync(ct);

    private static string ArtifactNumberDisplay(RequirementChange change) =>
        $"{change.BaseNumber}.{change.Revision:00}";
}
