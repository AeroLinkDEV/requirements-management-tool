using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// One exact requirement revision a test change request is allowed to govern through a procedure proposal.
///
/// Project and discipline are necessary boundaries, but they are not authority. Authority comes from the
/// package's own verification-impact work, and build membership comes from the exact requirement manifest.
/// Keeping both predicates here prevents the picker, mutation, and procedure materializer from disagreeing.
/// </summary>
public sealed record TestChangeReviewRequirementChoice(
    Guid Id, Guid RevisionId, string DisplayNumber, string Statement, RequirementLevel Level);

public static class TestChangeReviewRequirementScope
{
    /// <summary>
    /// Revalidates the exact parent decision at review submission. The picker
    /// and proposal route are advisory boundaries; a controlled-editing draft
    /// or another writer can still change the JSON before submit. This shared
    /// check therefore resolves the governed requirement scope and the
    /// carried procedure coverage again immediately before a signature is
    /// requested.
    /// </summary>
    public static async Task ValidateProcedureChangesForSubmissionAsync(
        AeroLinkDbContext db, TestChangeReview review, ILadderPolicy policy, CancellationToken ct)
    {
        if (review.ArtifactKey.Kind == VerificationArtifactKind.Procedure
            && review.ArtifactKey.Discipline != VerificationDiscipline.System)
        {
            await ValidateSoftwareProcedureChangesAsync(db, review, policy, ct);
            return;
        }
        var governedIds = (await ForReviewAsync(db, review, null, ct, policy))
            .Select(x => x.RevisionId).ToHashSet();
        var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, review.ProjectId, review.ReleaseId, ct);
        var requirementBaselineId = await EffectiveRequirementBaselineIdAsync(
            db, review.ProjectId, review.ReleaseId, ct);
        var governedRequirementIds = requirementBaselineId is null
            ? new HashSet<Guid>()
            : (await db.BaselineRequirements.AsNoTracking()
                .Where(x => x.BaselineId == requirementBaselineId.Value)
                .Select(x => x.RevisionId)
                .ToListAsync(ct)).ToHashSet();
        var procedures = await db.TestProcedures.AsNoTracking()
            .Where(x => x.ProjectId == review.ProjectId
                && (x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case))
            .ToDictionaryAsync(x => x.BaseNumber, StringComparer.OrdinalIgnoreCase, ct);
        var wantedLevel = policy.RequirementLevelFor(review.ProcedureLevel(policy));
        var artifactNoun = review.Discipline == TestChangeReviewDiscipline.System ? "System Procedure" : "software Case";

        foreach (var change in review.ProcedureChanges.Where(x => x.Kind != TestProcedureChangeKind.Retire))
        {
            var parentIds = ParseIds(change.ParentRevisionIdsJson, change.DisplayNumber, "exact parent");
            var drivingIds = ParseIds(change.DrivingRequirementRevisionIdsJson, change.DisplayNumber, "driving");
            var removedIds = ParseIds(change.RemovedRequirementRevisionIdsJson, change.DisplayNumber, "removed");
            ExactParentSelectionPolicy.Validate(
                VerificationProcedureParentPolicy.Classification(change.ParentKind), parentIds,
                change.DerivedRationale, artifactNoun);

            if (change.ParentKind == VerificationProcedureParentKind.Derived && drivingIds.Count != 0)
                throw new DomainException(
                    $"{change.DisplayNumber} is Derived but still names driving requirement revisions.");
            if (change.ParentKind == VerificationProcedureParentKind.Allocated
                && !drivingIds.All(parentIds.Contains))
                throw new DomainException(
                    $"{change.DisplayNumber} has driving requirement revisions outside its exact final parent selection.");
            if (change.Kind != TestProcedureChangeKind.Modify && removedIds.Count != 0)
                throw new DomainException(
                    $"{change.DisplayNumber} cannot remove requirement coverage because it is not a modification.");
            if (drivingIds.Intersect(removedIds).Any())
                throw new DomainException(
                    $"{change.DisplayNumber} both adds and removes the same requirement coverage.");

            var currentCoverage = new HashSet<Guid>();
            if (change.Kind == TestProcedureChangeKind.Modify)
            {
                if (!procedures.TryGetValue(change.BaseNumber, out var procedure)
                    || effectivity is null
                    || !effectivity.RevisionByProcedure.TryGetValue(procedure.Id, out var currentRevisionId))
                    throw new DomainException(
                        $"{change.DisplayNumber} does not name a procedure revision carried by the target build.");
                currentCoverage = (await db.TestCoverage.AsNoTracking()
                        // Coverage is only current for the target release when its exact requirement
                        // revision is selected by that release's effective requirement baseline. A
                        // carried procedure can retain links from an older build, but those links are
                        // not valid parents for this package unless the target manifest still selects
                        // them. Suspect lifecycle evidence is visible elsewhere, never an authored parent.
                        .Where(x => x.ProcedureRevisionId == currentRevisionId
                            && !x.IsSuspect
                            && governedRequirementIds.Contains(x.RequirementRevisionId))
                        .Select(x => x.RequirementRevisionId).ToListAsync(ct)).ToHashSet();
                var absent = removedIds.FirstOrDefault(x => !currentCoverage.Contains(x));
                if (absent != Guid.Empty)
                    throw new DomainException(
                        $"{change.DisplayNumber} cannot remove requirement revision {absent} because its predecessor does not cover it.");

                if (change.ParentKind == VerificationProcedureParentKind.Allocated
                    && !currentCoverage.SetEquals(parentIds.ToHashSet())
                    && string.IsNullOrWhiteSpace(change.CoverageChangeRationale))
                    throw new DomainException(
                        $"{change.DisplayNumber} changes requirement coverage without an approved rationale.");
            }

            // ParentRevisionIdsJson is the immutable full successor selection. Driving IDs and removals
            // are only the change delta; accepting a partial selection here would let controlled editing
            // submit a package whose materialized coverage silently differs from the approved decision.
            if (change.ParentKind == VerificationProcedureParentKind.Allocated)
            {
                var expected = change.Kind == TestProcedureChangeKind.Modify
                    ? currentCoverage.Except(removedIds).Concat(drivingIds).ToHashSet()
                    : drivingIds.ToHashSet();
                if (!parentIds.ToHashSet().SetEquals(expected))
                    throw new DomainException(
                        $"{change.DisplayNumber} does not carry the exact final parent selection for its coverage delta.");
            }

            var ids = parentIds.Concat(drivingIds).Concat(removedIds).Distinct().ToList();
            if (ids.Count == 0) continue;
            var rows = await (from revision in db.RequirementRevisions.AsNoTracking()
                              join artifact in db.Requirements.AsNoTracking()
                                  on revision.ArtifactId equals artifact.Id
                              where ids.Contains(revision.Id)
                              select new { revision.Id, artifact.ProjectId, artifact.Level })
                .ToDictionaryAsync(x => x.Id, ct);
            foreach (var id in ids)
            {
                if (!rows.TryGetValue(id, out var row))
                    throw new DomainException($"{change.DisplayNumber} names requirement revision {id}, which does not exist.");
                if (row.ProjectId != review.ProjectId)
                    throw new DomainException($"{change.DisplayNumber} names a requirement revision from another project.");
                if (row.Level != wantedLevel)
                    throw new DomainException(
                        $"{change.DisplayNumber} names a {row.Level} requirement, but this {review.Discipline} artifact requires {wantedLevel}.");
            }

            // A retained predecessor parent is valid even when the new
            // package's impact item only names the changed requirement. New
            // parents still have to come from the package's governed scope.
            var allowed = governedIds.Union(currentCoverage).ToHashSet();
            var outside = ids.FirstOrDefault(x => !allowed.Contains(x));
            if (outside != Guid.Empty)
                throw new DomainException(
                    $"{change.DisplayNumber} names requirement revision {outside}, which is outside the governed package/build scope.");
        }
    }

    /// <summary>
    /// Procedure packages use the same exact-parent-or-derived policy as Case/System packages, but their
    /// configured parent kind is an exact software Case revision rather than a requirement revision. The
    /// policy is kept here so picker, mutation, and submission cannot grow three subtly different XOR rules.
    /// </summary>
    private static async Task ValidateSoftwareProcedureChangesAsync(
        AeroLinkDbContext db, TestChangeReview review, ILadderPolicy policy, CancellationToken ct)
    {
        var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, review.ProjectId, review.ReleaseId, ct);
        var carriedCaseRevisionIds = effectivity?.RevisionIds.ToHashSet() ?? [];
        var caseRows = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                              join procedure in db.TestProcedures.AsNoTracking()
                                  on revision.ProcedureId equals procedure.Id
                              where procedure.ProjectId == review.ProjectId
                                  && procedure.ArtifactKind == VerificationArtifactKind.Case
                                  && procedure.Level == review.ProcedureLevel(policy)
                              select new { revision.Id, revision.Revision, procedure.BaseNumber }).ToDictionaryAsync(x => x.Id, ct);
        foreach (var change in review.ProcedureChanges.Where(x => x.Kind != TestProcedureChangeKind.Retire))
        {
            var parentIds = ParseIds(change.ParentRevisionIdsJson, change.DisplayNumber, "exact Case parent");
            ExactParentSelectionPolicy.Validate(
                VerificationProcedureParentPolicy.Classification(change.ParentKind), parentIds,
                change.DerivedRationale, "software Procedure");
            if (change.DrivingRequirementRevisionIdsJson is not ("" or "[]"))
            {
                var driving = ParseIds(change.DrivingRequirementRevisionIdsJson, change.DisplayNumber, "driving");
                if (driving.Count != 0)
                    throw new DomainException($"{change.DisplayNumber} is a software Procedure and cannot name requirement parents; select exact Case revisions instead.");
            }
            if (change.ParentKind != VerificationProcedureParentKind.Allocated) continue;
            foreach (var id in parentIds)
            {
                if (!caseRows.ContainsKey(id))
                    throw new DomainException($"{change.DisplayNumber} names Case revision {id}, which does not exist in this Project or level.");
                if (!carriedCaseRevisionIds.Contains(id))
                    throw new DomainException($"{change.DisplayNumber} names Case revision {id}, which is not selected by the target build's exact Case manifest.");
            }
        }
    }

    /// <summary>
    /// A retarget decision may be recorded while its draft successor is still being authored. If the target
    /// is not already an exact link on the effective revision, however, the decision cannot be submitted as
    /// LinkExisting: that would sign an in-place mutation which the materializer deliberately did not perform.
    /// Correlate the decision with the same review's ModifyExisting change and its complete final selection.
    /// </summary>
    public static async Task ValidateRetargetPlansForSubmissionAsync(
        AeroLinkDbContext db, TestChangeReview review, CancellationToken ct, ILadderPolicy? policy = null)
    {
        var retargets = await db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.TestChangeReviewId == review.Id
                && x.State == VerificationImpactState.Resolved
                && x.Outcome == VerificationImpactOutcome.ProcedureRetargeted)
            .ToListAsync(ct);
        var confirmations = await db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.TestChangeReviewId == review.Id
                && x.State == VerificationImpactState.Resolved
                && x.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed
                && x.ResolvedProcedureId != null)
            .ToListAsync(ct);
        if (retargets.Count == 0 && confirmations.Count == 0) return;

        var procedureIds = retargets.Where(x => x.ProcedureId is not null)
            .Select(x => x.ProcedureId!.Value).Distinct().ToList();
        procedureIds.AddRange(confirmations.Where(x => x.ResolvedProcedureId is not null)
            .Select(x => x.ResolvedProcedureId!.Value));
        procedureIds = procedureIds.Distinct().ToList();
        var procedures = await db.TestProcedures.AsNoTracking()
            .Where(x => procedureIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, review.ProjectId, review.ReleaseId, ct);
        await ValidatePreMaterializationCoverageConfirmationsAsync(db, review, confirmations, ct);
        foreach (var item in retargets)
        {
            if (item.ProcedureId is not Guid procedureId || !procedures.TryGetValue(procedureId, out var procedure))
                throw new DomainException("A retarget decision must identify the stranded verification artifact.");
            if (item.RetargetedRequirementRevisionId is not Guid targetId || targetId == Guid.Empty)
                throw new DomainException("A retarget decision must identify its exact requirement revision.");

            if (!await IsExactRetargetTargetInBuildAsync(db, review.ProjectId, review.ReleaseId,
                    procedureId, targetId, policy, ct))
                throw new DomainException(
                    $"{item.SubjectDisplayNumber} retargets to a requirement revision that is not selected in the target build's exact requirement baseline.");

            var alreadyLinked = false;
            if (effectivity?.RevisionByProcedure.TryGetValue(procedureId, out var effectiveRevisionId) == true)
                alreadyLinked = await db.TestCoverage.AsNoTracking().AnyAsync(x =>
                    x.ProcedureRevisionId == effectiveRevisionId && x.RequirementRevisionId == targetId, ct);
            if (alreadyLinked)
            {
                // LinkExisting is the honest action for a target that is already present, including a #709
                // suspect link that the resolution is confirming. No successor is required for this case.
                continue;
            }

            if (item.ProcedureChangeAction != TestProcedureChangeAction.ModifyExisting)
                throw new DomainException(
                    $"{item.SubjectDisplayNumber} retargets to a new exact parent and must use ModifyExisting with a controlled successor before review submission.");
            var matchingChange = review.ProcedureChanges.FirstOrDefault(x =>
                x.Kind == TestProcedureChangeKind.Modify
                && string.Equals(x.BaseNumber, procedure.BaseNumber, StringComparison.OrdinalIgnoreCase));
            if (matchingChange is null)
                throw new DomainException(
                    $"{item.SubjectDisplayNumber} retarget requires a ModifyExisting change for the same controlled artifact before review submission.");
            var parentIds = ParseIds(matchingChange.ParentRevisionIdsJson, matchingChange.DisplayNumber, "exact parent");
            if (!parentIds.Contains(targetId))
                throw new DomainException(
                    $"{matchingChange.DisplayNumber} must include retargeted requirement revision {targetId} in its full exact parent selection.");
        }

        foreach (var item in confirmations)
        {
            // A pre-materialisation Modify+LinkExisting confirmation was fully checked above against the
            // exact predecessor link. Its target revision is intentionally still null; materialisation will
            // bind it and clear the carried #709 suspect row. Do not run the post-materialisation branch on
            // that same legitimate decision.
            if (item.RequirementRevisionId is null)
                continue;
            if (item.RequirementRevisionId is not Guid targetId || item.ResolvedProcedureRevisionId is not Guid procedureRevisionId)
                // The pre-materialization case was handled above. Keeping this guard here makes a malformed
                // persisted decision fail closed if a future caller changes that phase-specific query.
                throw new DomainException(
                    $"{item.SubjectDisplayNumber} coverage confirmation must bind to an exact requirement revision before review submission.");
            if (await db.TestCoverage.AsNoTracking().AnyAsync(x =>
                    x.ProcedureRevisionId == procedureRevisionId && x.RequirementRevisionId == targetId, ct))
                continue;
            if (item.ResolvedProcedureId is not Guid procedureId
                || !procedures.TryGetValue(procedureId, out var procedure))
                throw new DomainException("A coverage decision must identify the controlled verification artifact.");
            if (item.ProcedureChangeAction != TestProcedureChangeAction.ModifyExisting)
                throw new DomainException(
                    $"{item.SubjectDisplayNumber} confirms a missing exact coverage link and must use ModifyExisting with a controlled successor before review submission.");
            var matchingChange = review.ProcedureChanges.FirstOrDefault(x =>
                x.Kind == TestProcedureChangeKind.Modify
                && string.Equals(x.BaseNumber, procedure.BaseNumber, StringComparison.OrdinalIgnoreCase));
            if (matchingChange is null)
                throw new DomainException(
                    $"{item.SubjectDisplayNumber} coverage confirmation requires a ModifyExisting change for the same controlled artifact before review submission.");
            var parentIds = ParseIds(matchingChange.ParentRevisionIdsJson, matchingChange.DisplayNumber, "exact parent");
            if (!parentIds.Contains(targetId))
                throw new DomainException(
                    $"{matchingChange.DisplayNumber} must include confirmed requirement revision {targetId} in its full exact parent selection.");
        }
    }

    private static async Task ValidatePreMaterializationCoverageConfirmationsAsync(
        AeroLinkDbContext db, TestChangeReview review,
        IReadOnlyCollection<VerificationImpactItem> confirmations, CancellationToken ct)
    {
        var pending = confirmations.Where(x => x.RequirementRevisionId is null).ToList();
        if (pending.Count == 0) return;

        var changeIds = pending.Where(x => x.RequirementChangeId is not null)
            .Select(x => x.RequirementChangeId!.Value).Distinct().ToList();
        var changes = await db.RequirementChanges.AsNoTracking()
            .Where(x => changeIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var sourceIds = changes.Values.Select(x => x.ChangeRequestId).Distinct().ToList();
        var sources = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => sourceIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ProjectId, x.TargetReleaseId })
            .ToDictionaryAsync(x => x.Id, ct);
        var baselineId = await EffectiveRequirementBaselineIdAsync(db, review.ProjectId, review.ReleaseId, ct);
        var predecessorByChange = baselineId is null
            ? new Dictionary<Guid, Guid>()
            : (await (from change in db.RequirementChanges.AsNoTracking()
                      join artifact in db.Requirements.AsNoTracking()
                          on new { ProjectId = review.ProjectId, BaseNumber = change.BaseNumber }
                          equals new { artifact.ProjectId, artifact.BaseNumber }
                      join member in db.BaselineRequirements.AsNoTracking()
                          on artifact.Id equals member.ArtifactId
                      where changeIds.Contains(change.Id) && member.BaselineId == baselineId.Value
                      select new { change.Id, member.RevisionId }).ToListAsync(ct))
                .GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.Select(y => y.RevisionId).Single());

        foreach (var item in pending)
        {
            if (item.RequirementChangeId is not Guid changeId
                || !changes.TryGetValue(changeId, out var change))
                throw new DomainException(
                    $"{item.SubjectDisplayNumber} coverage confirmation must identify its originating requirement change before materialization.");
            if (item.ChangeRequestId != change.ChangeRequestId
                || !sources.TryGetValue(change.ChangeRequestId, out var source)
                || source.ProjectId != review.ProjectId
                || source.TargetReleaseId != review.ReleaseId)
                throw new DomainException(
                    $"{item.SubjectDisplayNumber} coverage confirmation names a requirement change outside this review's project/build scope.");
            if (change.Kind != RequirementChangeKind.Modify
                || !predecessorByChange.TryGetValue(changeId, out var predecessorRevisionId))
                throw new DomainException(
                    $"{item.SubjectDisplayNumber} coverage confirmation must wait for the exact requirement revision before review submission; an Introduce change has no predecessor exact link to confirm.");
            if (item.ProcedureChangeAction != TestProcedureChangeAction.LinkExisting
                || item.ResolvedProcedureRevisionId is not Guid procedureRevisionId
                || !await db.TestCoverage.AsNoTracking().AnyAsync(x =>
                    x.ProcedureRevisionId == procedureRevisionId
                    && x.RequirementRevisionId == predecessorRevisionId, ct))
                throw new DomainException(
                    $"{item.SubjectDisplayNumber} may be submitted before materialization only as LinkExisting when the selected approved revision covers its exact predecessor; otherwise wait for the target revision and use ModifyExisting.");
        }
    }

    /// <summary>
    /// The exact requirement revisions this package may govern, intersected with the build's requirement
    /// manifest. Used by both the picker projection and the mutation enforcement so they cannot disagree.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> CarriedImpactRevisionIdsAsync(
        AeroLinkDbContext db, TestChangeReview review, Guid? baselineId, CancellationToken ct)
    {
        var effectiveBaselineId = baselineId ?? await EffectiveRequirementBaselineIdAsync(
            db, review.ProjectId, review.ReleaseId, ct);
        if (effectiveBaselineId is null) return [];

        var baseline = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.Id == effectiveBaselineId && x.ProjectId == review.ProjectId
                && x.RequirementsMaterializedAt != null)
            .Select(x => new { x.Id }).SingleOrDefaultAsync(ct);
        if (baseline is null) return [];

        var items = await db.VerificationImpactItems.AsNoTracking()
            .Where(x => x.TestChangeReviewId == review.Id && x.ProjectId == review.ProjectId
                && x.ReleaseId == review.ReleaseId && x.State != VerificationImpactState.Superseded)
            .Select(x => new { x.RequirementRevisionId, x.RetargetedRequirementRevisionId })
            .ToListAsync(ct);
        var impactRevisionIds = items
            .SelectMany(x => new[] { x.RequirementRevisionId, x.RetargetedRequirementRevisionId })
            .Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        if (impactRevisionIds.Count == 0) return [];

        var carriedRevisionIds = await db.BaselineRequirements.AsNoTracking()
            .Where(x => x.BaselineId == baseline.Id && impactRevisionIds.Contains(x.RevisionId))
            .Select(x => x.RevisionId).Distinct().ToListAsync(ct);
        return carriedRevisionIds;
    }

    public static IQueryable<TestChangeReviewRequirementChoice> ChoicesQuery(
        AeroLinkDbContext db, Guid projectId, IReadOnlyCollection<Guid> carriedRevisionIds,
        TestProcedureLevel procedureLevel, ILadderPolicy? policy = null)
    {
        var ids = carriedRevisionIds.Distinct().ToList();
        var wantedLevel = (policy ?? LegacyLadderPolicy.Instance).RequirementLevelFor(procedureLevel);
        return from revision in db.RequirementRevisions.AsNoTracking()
                   .Where(x => ids.Contains(x.Id))
               join artifact in db.Requirements.AsNoTracking()
                   .Where(x => x.ProjectId == projectId && x.Level == wantedLevel)
                      on revision.ArtifactId equals artifact.Id
               orderby artifact.BaseNumber, revision.Revision
               select new TestChangeReviewRequirementChoice(
                   artifact.Id,
                   revision.Id,
                   artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                   revision.Statement,
                   artifact.Level);
    }

    public static async Task<IReadOnlyList<TestChangeReviewRequirementChoice>> ForReviewAsync(
        AeroLinkDbContext db, TestChangeReview review, Guid? baselineId, CancellationToken ct,
        ILadderPolicy? policy = null) =>
        await ChoicesQuery(db, review.ProjectId,
            await CarriedImpactRevisionIdsAsync(db, review, baselineId, ct), review.ProcedureLevel(policy), policy)
            .ToListAsync(ct);

    public static async Task<(int Total, IReadOnlyList<TestChangeReviewRequirementChoice> Items)> ForReviewPageAsync(
        AeroLinkDbContext db, TestChangeReview review, string? search, int page, int pageSize,
        IReadOnlyCollection<Guid>? hydrateRevisionIds, CancellationToken ct, ILadderPolicy? policy = null)
    {
        var carried = await CarriedImpactRevisionIdsAsync(db, review, null, ct);
        // The governed candidate set is the package's own scope, so materializing it is bounded by the
        // change's actual reach, never the whole Project. Filtering and paging then run in memory because
        // DisplayNumber is a computed projection property EF cannot translate into SQL.
        var scoped = await ChoicesQuery(db, review.ProjectId, carried, review.ProcedureLevel(policy), policy).ToListAsync(ct);
        var query = scoped.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim().ToLower();
            query = query.Where(x => x.DisplayNumber.ToLower().Contains(q) || x.Statement.ToLower().Contains(q));
        }
        var total = query.Count();
        var paged = query.OrderBy(x => x.DisplayNumber).Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var requested = (hydrateRevisionIds ?? []).Distinct().ToList();
        var hydrated = requested.Count == 0
            ? []
            : scoped.Where(x => requested.Contains(x.RevisionId)).ToList();
        var items = paged.Concat(hydrated).DistinctBy(x => x.RevisionId)
            .OrderBy(x => x.DisplayNumber).ToList();
        return (total, items);
    }

    public static async Task<Guid?> EffectiveRequirementBaselineIdAsync(AeroLinkDbContext db, Guid projectId,
        Guid releaseId, CancellationToken ct)
    {
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId)
            .Select(x => new { x.Id, x.PredecessorReleaseId }).ToListAsync(ct);
        var baselines = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.RequirementsMaterializedAt != null)
            .Select(x => new { x.Id, x.ReleaseId, x.FrozenAt, x.CreatedAt }).ToListAsync(ct);
        var current = releases.SingleOrDefault(x => x.Id == releaseId);
        var visited = new HashSet<Guid>();
        while (current is not null && visited.Add(current.Id))
        {
            // DateTimeOffset ordering stays in memory for SQLite compatibility.
            var baseline = baselines.Where(x => x.ReleaseId == current.Id)
                .OrderByDescending(x => x.FrozenAt ?? x.CreatedAt).FirstOrDefault();
            if (baseline is not null) return baseline.Id;
            current = current.PredecessorReleaseId is null
                ? null
                : releases.SingleOrDefault(x => x.Id == current.PredecessorReleaseId.Value);
        }
        return null;
    }

    /// <summary>
    /// Resolves the only requirement revision a retarget decision may name: an active exact revision selected
    /// in the target release's current materialized requirement baseline, at the configured level verified by
    /// the named System Procedure or software Case. Artifact activity alone is insufficient because an older
    /// sibling-baseline revision can remain active after a later build selects its successor.
    /// </summary>
    public static async Task<bool> IsExactRetargetTargetInBuildAsync(
        AeroLinkDbContext db, Guid projectId, Guid releaseId, Guid procedureId,
        Guid requirementRevisionId, ILadderPolicy? policy, CancellationToken ct)
    {
        var procedure = await db.TestProcedures.AsNoTracking()
            .Where(x => x.Id == procedureId && x.ProjectId == projectId)
            .Select(x => new { x.Level, x.ArtifactKind }).SingleOrDefaultAsync(ct);
        if (procedure is null
            || (procedure.Level == TestProcedureLevel.System
                ? procedure.ArtifactKind != VerificationArtifactKind.Procedure
                : procedure.ArtifactKind is not (VerificationArtifactKind.Case
                    or VerificationArtifactKind.Procedure)))
            return false;

        RequirementLevel requiredLevel;
        try { requiredLevel = (policy ?? LegacyLadderPolicy.Instance).RequirementLevelFor(procedure.Level); }
        catch (DomainException) { return false; }

        var baselineId = await EffectiveRequirementBaselineIdAsync(db, projectId, releaseId, ct);
        if (baselineId is null) return false;
        return await (from member in db.BaselineRequirements.AsNoTracking()
                      join revision in db.RequirementRevisions.AsNoTracking()
                          on member.RevisionId equals revision.Id
                      join artifact in db.Requirements.AsNoTracking()
                          on revision.ArtifactId equals artifact.Id
                      where member.BaselineId == baselineId.Value
                          && member.RevisionId == requirementRevisionId
                          && revision.State == RequirementRevisionState.Active
                          && artifact.ProjectId == projectId
                          && artifact.Level == requiredLevel
                      select member.Id).AnyAsync(ct);
    }

    private static IReadOnlyList<Guid> ParseIds(string json, string displayNumber, string kind)
    {
        try
        {
            return ExactParentSelectionPolicy.NormalizeIds(
                System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(string.IsNullOrWhiteSpace(json) ? "[]" : json)
                    ?? [], $"{displayNumber} {kind} revisions");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new DomainException($"{displayNumber} carries malformed {kind} requirement revisions.");
        }
    }
}
