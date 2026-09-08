using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

internal sealed class SaveBoundaryIntegrityValidator(AeroLinkDbContext db)
{
    private readonly AeroLinkDbContext _db = db;

    internal async Task ValidateAsync(CancellationToken ct)
    {
        await ValidateTestChangeReviewOriginsAsync(ct);
        await ValidateReferencedCaseChangesAsync(ct);
        await ValidateReferencedCaseAssessmentsAsync(ct);
        await ValidateReferencedCaseAssessmentParentsAsync(ct);
        await ValidateReferencedCaseReviewsAsync(ct);
        await ValidateRequirementParentSelectionsAsync(ct);
        ValidateCaseProcedureLinkIdentityIntegrity();
        await ValidateProcedureHeadersAsync(ct);
        await RefuseCrossLevelCoverageAsync(ct);
        await ValidateProcedureParentsAsync(ct);
        await ValidateCaseSystemParentSelectionsAsync(ct);
        await ValidateCaseProcedureLinksAsync(ct);
        await ValidateExactLinkLifecycleIntegrityAsync(ct);
        await ValidateExecutionCutoverProvenanceIntegrityAsync(ct);
    }

    /// <summary>
    /// Application-side integrity for the governed #726 provenance records. The database enforces the same
    /// relationships with real foreign keys and PostgreSQL triggers; this save-boundary check gives SQLite
    /// and every application caller the same fail-closed decision without relying on EF alone.
    /// </summary>
    private async Task ValidateExecutionCutoverProvenanceIntegrityAsync(CancellationToken ct)
    {
        var trackedEvents = _db.ChangeTracker.Entries<BaselineEvent>()
            .Where(x => x.State != EntityState.Deleted)
            .ToDictionary(x => x.Entity.Id, x => x.Entity);
        var trackedProcedures = _db.ChangeTracker.Entries<TestProcedure>()
            .Where(x => x.State != EntityState.Deleted)
            .ToDictionary(x => x.Entity.Id, x => x.Entity);
        var trackedRevisions = _db.ChangeTracker.Entries<TestProcedureRevision>()
            .Where(x => x.State != EntityState.Deleted)
            .ToDictionary(x => x.Entity.Id, x => x.Entity);
        var provenance = _db.ChangeTracker.Entries<BaselineExecutionCutoverProvenance>().ToList();
        foreach (var entry in provenance)
        {
            // Controlled provenance is immutable historical evidence: an application/SQLite write must fail
            // exactly like the PostgreSQL BEFORE UPDATE/DELETE triggers.
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new DomainException(
                    "Execution cutover provenance is immutable historical evidence; it cannot be modified or deleted.");
            if (entry.State != EntityState.Added) continue;
            var row = entry.Entity;
            BaselineEvent? linkedEvent = trackedEvents.GetValueOrDefault(row.EventId);
            if (linkedEvent is null)
                linkedEvent = await _db.BaselineEvents.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == row.EventId, ct);
            if (linkedEvent is null)
                throw new DomainException(
                    "Execution cutover provenance must reference an existing baseline event.");
            if (linkedEvent.BaselineId != row.BaselineId)
                throw new DomainException(
                    "Execution cutover provenance event must belong to the same baseline as the provenance row.");
            if (linkedEvent.EventType != "ExecutionCutoverManifestMigrated")
                throw new DomainException(
                    "Execution cutover provenance must reference an ExecutionCutoverManifestMigrated summary event.");
        }

        var sources = _db.ChangeTracker.Entries<TestProcedureMigrationSource>().ToList();
        foreach (var entry in sources)
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new DomainException(
                    "Procedure migration sources are immutable historical evidence; they cannot be modified or deleted.");
            if (entry.State != EntityState.Added) continue;
            var source = entry.Entity;
            var sourceRevision = trackedRevisions.GetValueOrDefault(source.SourceCaseRevisionId);
            TestProcedure? sourceOwner = null;
            if (sourceRevision is not null)
                sourceOwner = trackedProcedures.GetValueOrDefault(sourceRevision.ProcedureId);
            if (sourceRevision is null || sourceOwner is null)
            {
                var persisted = await (from revision in _db.TestProcedureRevisions.AsNoTracking()
                                       join procedure in _db.TestProcedures.AsNoTracking()
                                           on revision.ProcedureId equals procedure.Id
                                       where revision.Id == source.SourceCaseRevisionId
                                       select new { Revision = revision, Procedure = procedure })
                    .SingleOrDefaultAsync(ct);
                if (persisted is null)
                    throw new DomainException(
                        "A Procedure migration source must reference an existing source revision.");
                sourceRevision = persisted.Revision;
                sourceOwner = persisted.Procedure;
            }
            if (sourceOwner.ProjectId != source.ProjectId
                || sourceOwner.ArtifactKind != VerificationArtifactKind.Case
                || sourceOwner.Level == TestProcedureLevel.System)
                throw new DomainException(
                    "A Procedure migration source Case revision must be a software Case in the stated project.");
            var generated = trackedProcedures.GetValueOrDefault(source.GeneratedProcedureArtifactId);
            if (generated is null)
                generated = await _db.TestProcedures.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == source.GeneratedProcedureArtifactId, ct);
            if (generated is null)
                throw new DomainException(
                    "A Procedure migration source must reference an existing generated artifact.");
            if (generated.ProjectId != source.ProjectId
                || generated.ArtifactKind != VerificationArtifactKind.Procedure)
                throw new DomainException(
                    "A Procedure migration source generated artifact must be a Procedure in the stated project.");
            var generatedRevision = trackedRevisions.GetValueOrDefault(source.GeneratedProcedureRevisionId);
            if (generatedRevision is null)
                generatedRevision = await _db.TestProcedureRevisions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == source.GeneratedProcedureRevisionId, ct);
            if (generatedRevision is null)
                throw new DomainException(
                    "A Procedure migration source must reference an existing generated revision.");
            if (generatedRevision.ProcedureId != source.GeneratedProcedureArtifactId)
                throw new DomainException(
                    "A Procedure migration source generated revision must belong to its generated artifact.");
        }

        var cleanupEvidence = _db.ChangeTracker.Entries<RollbackCleanupFailureEvidence>().ToList();
        foreach (var entry in cleanupEvidence)
        {
            // A later successful startup never erases the historical fact that cleanup failed: the evidence
            // is immutable controlled reconciliation evidence in the application and at the database.
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new DomainException(
                    "Rollback cleanup failure evidence is immutable historical evidence; it cannot be modified or deleted.");
        }
    }

    private void ValidateCaseProcedureLinkIdentityIntegrity()
    {
        foreach (var entry in _db.ChangeTracker.Entries<TestCaseProcedureLink>()
                     .Where(x => x.State == EntityState.Modified))
        {
            if (entry.Property(x => x.CaseRevisionId).IsModified
                || entry.Property(x => x.ProcedureRevisionId).IsModified)
                throw new DomainException("An exact Case-to-Procedure relation cannot be retargeted; create a successor relation.");
            var originalLifecycleId = entry.Property(x => x.ExactLinkSuspectLifecycleId).OriginalValue;
            if (entry.Entity.ExactLinkSuspectLifecycleId != originalLifecycleId)
                throw new DomainException("A Case-to-Procedure relation cannot change its immutable suspect lifecycle association.");
        }
    }

    /// <summary>
    /// Keeps #709's shared projection/event contract exact for every registered link kind. Lifecycle state is
    /// mutable only through attributed transitions; the link, cause and already-written events are immutable.
    /// Case-to-Procedure additionally binds the cause to the carried Case revision rather than merely to any
    /// verification revision in the project.
    /// </summary>
    private async Task ValidateExactLinkLifecycleIntegrityAsync(CancellationToken ct)
    {
        var changedLifecycles = _db.ChangeTracker.Entries<ExactLinkSuspectLifecycle>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        if (changedLifecycles.Any(x => x.State == EntityState.Deleted))
            throw new DomainException("An exact-link lifecycle projection and its attributed history cannot be deleted.");
        foreach (var entry in changedLifecycles.Where(x => x.State == EntityState.Modified))
        {
            var immutable = new[]
            {
                nameof(ExactLinkSuspectLifecycle.ProjectId), nameof(ExactLinkSuspectLifecycle.LinkKind),
                nameof(ExactLinkSuspectLifecycle.LinkId), nameof(ExactLinkSuspectLifecycle.CauseKind),
                nameof(ExactLinkSuspectLifecycle.CauseRequirementRevisionId),
                nameof(ExactLinkSuspectLifecycle.CauseBaselineImportId),
                nameof(ExactLinkSuspectLifecycle.CauseVerificationRevisionId),
                nameof(ExactLinkSuspectLifecycle.RaisedBy), nameof(ExactLinkSuspectLifecycle.RaisedAt),
                nameof(ExactLinkSuspectLifecycle.RaisedRationale),
            };
            if (immutable.Any(name => entry.Property(name).IsModified))
                throw new DomainException("An exact-link lifecycle's identity, cause, and raised attribution are immutable.");
        }

        if (_db.ChangeTracker.Entries<ExactLinkSuspectEvent>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new DomainException("Exact-link lifecycle events are append-only and cannot be changed or deleted.");

        var lifecycleById = changedLifecycles.ToDictionary(x => x.Entity.Id, x => x.Entity);
        var addedEvents = _db.ChangeTracker.Entries<ExactLinkSuspectEvent>()
            .Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToList();
        var missingLifecycleIds = addedEvents.Select(x => x.LifecycleId)
            .Where(x => !lifecycleById.ContainsKey(x)).Distinct().ToList();
        if (missingLifecycleIds.Count > 0)
        {
            var persisted = await _db.ExactLinkSuspectLifecycles.AsNoTracking()
                .Where(x => missingLifecycleIds.Contains(x.Id)).ToListAsync(ct);
            foreach (var lifecycle in persisted) lifecycleById[lifecycle.Id] = lifecycle;
        }
        foreach (var item in addedEvents)
        {
            if (!lifecycleById.TryGetValue(item.LifecycleId, out var lifecycle)
                || item.ProjectId != lifecycle.ProjectId || item.LinkKind != lifecycle.LinkKind
                || item.LinkId != lifecycle.LinkId || item.CauseKind != lifecycle.CauseKind
                || item.CauseRequirementRevisionId != lifecycle.CauseRequirementRevisionId
                || item.CauseBaselineImportId != lifecycle.CauseBaselineImportId
                || item.CauseVerificationRevisionId != lifecycle.CauseVerificationRevisionId)
                throw new DomainException("An exact-link event must retain its lifecycle's exact link, cause, and project attribution.");
        }

        var addedRequirementCauseIds = changedLifecycles
            .Where(x => x.State == EntityState.Added
                && x.Entity.LinkKind == ExactLinkKind.RequirementTrace
                && x.Entity.CauseKind == ExactLinkLifecycleCauseKind.InternalRequirementRevision)
            .Select(x => x.Entity.CauseRequirementRevisionId!.Value).Distinct().ToHashSet();
        if (addedRequirementCauseIds.Count > 0)
        {
            var known = (await _db.RequirementRevisions.AsNoTracking()
                    .Where(x => addedRequirementCauseIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct))
                .ToHashSet();
            known.UnionWith(_db.ChangeTracker.Entries<RequirementRevision>()
                .Where(x => x.State != EntityState.Deleted && addedRequirementCauseIds.Contains(x.Entity.Id))
                .Select(x => x.Entity.Id));
            if (!known.SetEquals(addedRequirementCauseIds))
                throw new DomainException("An internal exact-link cause must name an existing requirement revision when it is raised.");
        }

        var caseLifecycles = changedLifecycles.Select(x => x.Entity)
            .Where(x => x.LinkKind == ExactLinkKind.CaseProcedure).ToList();
        if (caseLifecycles.Count == 0) return;
        var linkIds = caseLifecycles.Select(x => x.LinkId).Distinct().ToList();
        var links = await _db.TestCaseProcedureLinks.AsNoTracking()
            .Where(x => linkIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var tracked in _db.ChangeTracker.Entries<TestCaseProcedureLink>()
                     .Where(x => x.State != EntityState.Deleted && linkIds.Contains(x.Entity.Id)))
            links[tracked.Entity.Id] = tracked.Entity;
        foreach (var lifecycle in caseLifecycles)
        {
            if (!links.TryGetValue(lifecycle.LinkId, out var link)
                || link.ExactLinkSuspectLifecycleId != lifecycle.Id
                || lifecycle.CauseKind != ExactLinkLifecycleCauseKind.InternalVerificationRevision
                || lifecycle.CauseVerificationRevisionId != link.CaseRevisionId)
                throw new DomainException("A Case-to-Procedure lifecycle must identify its exact carried link and changed Case revision.");
        }
    }

    /// <summary>
    /// Save-boundary protection for the polymorphic Case-origin reference. The database trigger protects
    /// PostgreSQL direct writes; this check gives SQLite and every application caller the same fail-closed
    /// decision without adding parallel nullable origin columns to the aggregate.
    /// </summary>
    private async Task ValidateTestChangeReviewOriginsAsync(CancellationToken ct)
    {
        var entries = _db.ChangeTracker.Entries<TestChangeReview>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified
                && x.Entity.OriginKind is TestChangeReviewOriginKind.CaseChange
                    or TestChangeReviewOriginKind.CaseAssessment or TestChangeReviewOriginKind.CaseReview)
            .Select(x => x.Entity).ToList();
        foreach (var review in entries)
        {
            if (review.ArtifactKey.Kind != VerificationArtifactKind.Procedure
                || review.ArtifactKey.Discipline == VerificationDiscipline.System)
                throw new DomainException("A Case origin is valid only for a software Procedure package.");

            if (review.OriginKind == TestChangeReviewOriginKind.CaseChange)
            {
                var reviewEntry = _db.ChangeTracker.Entries<TestChangeReview>()
                    .Single(x => ReferenceEquals(x.Entity, review));
                var source = await (from change in _db.Set<TestProcedureChange>().AsNoTracking()
                                     join parent in _db.TestChangeReviews.AsNoTracking()
                                         on change.TestChangeReviewId equals parent.Id
                                     where change.Id == review.OriginReferenceId
                                     select new
                                     {
                                         parent.ProjectId, parent.ReleaseId, parent.Discipline, parent.ArtifactKind,
                                         parent.State, change.BaseNumber, change.Revision
                                     }).SingleOrDefaultAsync(ct);
                var sourceStateEligible = source is not null
                    && (source.State == TestChangeReviewState.Approved
                        || (source.State == TestChangeReviewState.Superseded
                            && (review.Revision > 0 || reviewEntry.State != EntityState.Added)));
                if (source is null || source.ProjectId != review.ProjectId || source.ReleaseId != review.ReleaseId
                    || source.Discipline != review.Discipline || source.ArtifactKind != VerificationArtifactKind.Case
                    || !sourceStateEligible
                    || string.IsNullOrWhiteSpace(source.BaseNumber))
                    throw new DomainException(review.Revision == 0 && review.State != TestChangeReviewState.Superseded
                        ? "A Case-change origin must be an approved exact software Case change in this project and build."
                        : "A Procedure revision must retain its exact approved or superseded Case-change origin.");
                var identity = $"{source.BaseNumber}.{source.Revision:D2}";
                if (!string.Equals(review.SourceCaseOriginNumber, identity, StringComparison.Ordinal))
                    throw new DomainException("A Case-change origin must retain the exact Case change identity.");
            }
            else if (review.OriginKind == TestChangeReviewOriginKind.CaseReview)
            {
                var reviewEntry = _db.ChangeTracker.Entries<TestChangeReview>()
                    .Single(x => ReferenceEquals(x.Entity, review));
                var source = await _db.TestChangeReviews.AsNoTracking()
                    .Where(x => x.Id == review.OriginReferenceId)
                    .Select(x => new
                    {
                        x.ProjectId, x.ReleaseId, x.Discipline, x.ArtifactKind, x.State,
                        x.BaseNumber, x.Revision,
                    }).SingleOrDefaultAsync(ct);
                var sourceStateEligible = source is not null
                    && (source.State == TestChangeReviewState.Approved
                        || (source.State == TestChangeReviewState.Superseded
                            && (review.Revision > 0 || reviewEntry.State != EntityState.Added)));
                if (source is null || source.ProjectId != review.ProjectId || source.ReleaseId != review.ReleaseId
                    || source.Discipline != review.Discipline
                    || source.ArtifactKind != VerificationArtifactKind.Case || !sourceStateEligible)
                    throw new DomainException("A Case-review origin must be an approved exact software Case package in this project and build.");
                var identity = $"{source.BaseNumber}.{source.Revision:D2}";
                if (!string.Equals(review.SourceCaseOriginNumber, identity, StringComparison.Ordinal))
                    throw new DomainException("A Case-review origin must retain the exact Case package identity.");
            }
            else
            {
                var reviewEntry = _db.ChangeTracker.Entries<TestChangeReview>()
                    .Single(x => ReferenceEquals(x.Entity, review));
                var source = await (from item in _db.VerificationImpactItems.AsNoTracking()
                                     join parent in _db.TestChangeReviews.AsNoTracking()
                                         on item.TestChangeReviewId equals parent.Id
                                     where item.Id == review.OriginReferenceId
                                     select new
                                     {
                                         item.ProjectId, item.ReleaseId, parent.Discipline, parent.ArtifactKind,
                                         parentState = parent.State, itemState = item.State, item.Outcome, item.ProcedureChangeAction,
                                         item.RequirementRevisionId, item.SubjectDisplayNumber
                                     }).SingleOrDefaultAsync(ct);
                if (source is null || source.ProjectId != review.ProjectId || source.ReleaseId != review.ReleaseId
                    || source.Discipline != review.Discipline || source.ArtifactKind != VerificationArtifactKind.Case
                     || (source.parentState == TestChangeReviewState.Superseded
                         && review.Revision == 0 && reviewEntry.State == EntityState.Added)
                    || source.itemState != VerificationImpactState.Resolved
                    || source.Outcome != VerificationImpactOutcome.NewProcedureRequired
                    || source.ProcedureChangeAction != TestProcedureChangeAction.CreateNew
                    || source.RequirementRevisionId is null)
                    throw new DomainException("A Case-assessment origin must be a resolved exact assessment that found new Procedure work.");
                if (review.Revision == 0 && reviewEntry.State == EntityState.Added)
                {
                    var baselineId = await TestChangeReviewRequirementScope.EffectiveRequirementBaselineIdAsync(
                        _db, review.ProjectId, review.ReleaseId, ct);
                    if (baselineId is null || !await _db.BaselineRequirements.AsNoTracking()
                            .AnyAsync(x => x.BaselineId == baselineId && x.RevisionId == source.RequirementRevisionId, ct))
                        throw new DomainException("A Case-assessment origin must be bound to this build's effective baseline.");
                }
                if (!string.Equals(review.SourceCaseOriginNumber, source.SubjectDisplayNumber, StringComparison.Ordinal))
                    throw new DomainException("A Case-assessment origin must retain the exact assessment identity.");
            }
        }
    }

    /// <summary>The approved Case package is the immutable aggregate origin of its one Procedure assessment.</summary>
    private async Task ValidateReferencedCaseReviewsAsync(CancellationToken ct)
    {
        var changed = _db.ChangeTracker.Entries<TestChangeReview>()
            .Where(x => x.State is EntityState.Modified or EntityState.Deleted)
            .Where(x => x.State == EntityState.Deleted || new[]
            {
                nameof(TestChangeReview.ProjectId), nameof(TestChangeReview.ReleaseId),
                nameof(TestChangeReview.Discipline), nameof(TestChangeReview.ArtifactKind),
                nameof(TestChangeReview.BaseNumber), nameof(TestChangeReview.Revision),
                nameof(TestChangeReview.ChangeRequestId), nameof(TestChangeReview.State),
            }.Any(name => x.Property(name).IsModified)).ToList();
        var ids = changed.Select(x => x.Entity.Id).ToHashSet();
        if (ids.Count == 0) return;
        var referenced = (await _db.TestChangeReviews.AsNoTracking()
                .Where(x => x.OriginKind == TestChangeReviewOriginKind.CaseReview
                    && ids.Contains(x.OriginReferenceId))
                .Select(x => x.OriginReferenceId).ToListAsync(ct))
            .Concat(_db.ChangeTracker.Entries<TestChangeReview>()
                .Where(x => x.State != EntityState.Deleted
                    && x.Entity.OriginKind == TestChangeReviewOriginKind.CaseReview
                    && ids.Contains(x.Entity.OriginReferenceId))
                .Select(x => x.Entity.OriginReferenceId)).ToHashSet();
        foreach (var entry in changed.Where(x => referenced.Contains(x.Entity.Id)))
        {
            if (entry.State == EntityState.Deleted)
                throw new DomainException("A Case package that supplies a Procedure assessment origin cannot be deleted.");
            var stateChanged = entry.Property(nameof(TestChangeReview.State)).IsModified
                && !Equals(entry.Property(nameof(TestChangeReview.State)).OriginalValue, entry.Entity.State);
            var historicalAdvance = Equals(entry.Property(nameof(TestChangeReview.State)).OriginalValue,
                    TestChangeReviewState.Approved)
                && entry.Entity.State == TestChangeReviewState.Superseded;
            var identityChanged = new[]
            {
                nameof(TestChangeReview.ProjectId), nameof(TestChangeReview.ReleaseId),
                nameof(TestChangeReview.Discipline), nameof(TestChangeReview.ArtifactKind),
                nameof(TestChangeReview.BaseNumber), nameof(TestChangeReview.Revision),
                nameof(TestChangeReview.ChangeRequestId),
            }.Any(name => entry.Property(name).IsModified);
            if (identityChanged || stateChanged && !historicalAdvance)
                throw new DomainException("A Case package used as a Procedure assessment origin is immutable.");
        }
    }

    /// <summary>
    /// A resolved Case assessment is an immutable origin once a Procedure package names it. Ordinary operational
    /// writes (for example a timestamp or version touch) remain possible, but changing its eligibility or exact
    /// identity would make the already-issued Procedure package point at a different decision.
    /// </summary>
    private async Task ValidateReferencedCaseAssessmentsAsync(CancellationToken ct)
    {
        var changed = _db.ChangeTracker.Entries<VerificationImpactItem>()
            .Where(x => x.State is EntityState.Modified or EntityState.Deleted)
            .Where(x => x.State == EntityState.Deleted ||
                new[] { "ProjectId", "ReleaseId", "TestChangeReviewId", "RequirementChangeId", "RequirementRevisionId",
                    "SubjectDisplayNumber", "State", "Outcome", "ProcedureChangeAction" }
                    .Any(name => x.Property(name).IsModified))
            .Select(x => x.Entity.Id)
            .Where(x => x != Guid.Empty)
            .ToHashSet();
        if (changed.Count == 0) return;

        var persisted = await _db.TestChangeReviews.AsNoTracking()
            .Where(x => x.ArtifactKind == VerificationArtifactKind.Procedure
                && x.OriginKind == TestChangeReviewOriginKind.CaseAssessment
                && changed.Contains(x.OriginReferenceId))
            .Select(x => x.OriginReferenceId)
            .ToListAsync(ct);
        var tracked = _db.ChangeTracker.Entries<TestChangeReview>()
            .Where(x => x.State != EntityState.Deleted
                && x.Entity.ArtifactKind == VerificationArtifactKind.Procedure
                && x.Entity.OriginKind == TestChangeReviewOriginKind.CaseAssessment
                && changed.Contains(x.Entity.OriginReferenceId))
            .Select(x => x.Entity.OriginReferenceId);
        if (persisted.Concat(tracked).Any())
            throw new DomainException("A Case assessment referenced by a Procedure package is immutable; raise a new assessment instead.");
    }

    /// <summary>
    /// The assessment item is not the whole source identity: its owning Case review also supplies the
    /// project/build/discipline/kind and lifecycle context. Protect that parent for the same lifetime as the
    /// assessment itself, while permitting only the deliberate Approved -> Superseded historical transition.
    /// </summary>
    private async Task ValidateReferencedCaseAssessmentParentsAsync(CancellationToken ct)
    {
        var changedParents = _db.ChangeTracker.Entries<TestChangeReview>()
            .Where(x => x.State is EntityState.Modified or EntityState.Deleted)
            .Where(x => x.State == EntityState.Deleted ||
                new[] { "ProjectId", "ReleaseId", "Discipline", "ArtifactKind", "BaseNumber", "Revision", "ChangeRequestId", "State" }
                    .Any(name => x.Property(name).IsModified))
            .ToList();
        var changedParentIds = changedParents.Select(x => x.Entity.Id).Where(x => x != Guid.Empty).ToHashSet();
        if (changedParentIds.Count == 0) return;

        var assessmentSources = await _db.VerificationImpactItems.AsNoTracking()
            .Where(x => changedParentIds.Contains(x.TestChangeReviewId))
            .Select(x => new { x.Id, x.TestChangeReviewId })
            .ToListAsync(ct);
        if (assessmentSources.Count == 0) return;
        var assessmentIds = assessmentSources.Select(x => x.Id).ToHashSet();

        var persistedReferences = await _db.TestChangeReviews.AsNoTracking()
            .Where(x => x.ArtifactKind == VerificationArtifactKind.Procedure
                && x.OriginKind == TestChangeReviewOriginKind.CaseAssessment
                && assessmentIds.Contains(x.OriginReferenceId))
            .Select(x => x.OriginReferenceId)
            .ToHashSetAsync(ct);
        var trackedReferences = _db.ChangeTracker.Entries<TestChangeReview>()
            .Where(x => x.State != EntityState.Deleted
                && x.Entity.ArtifactKind == VerificationArtifactKind.Procedure
                && x.Entity.OriginKind == TestChangeReviewOriginKind.CaseAssessment
                && assessmentIds.Contains(x.Entity.OriginReferenceId))
            .Select(x => x.Entity.OriginReferenceId)
            .ToHashSet();
        var referencedParentIds = assessmentSources
            .Where(x => persistedReferences.Contains(x.Id) || trackedReferences.Contains(x.Id))
            .Select(x => x.TestChangeReviewId)
            .ToHashSet();
        foreach (var entry in changedParents.Where(x => referencedParentIds.Contains(x.Entity.Id)))
        {
            if (entry.State == EntityState.Deleted)
                throw new DomainException("A Case package that supplies a Procedure assessment origin cannot be deleted.");
            var stateChanged = entry.Property(nameof(TestChangeReview.State)).IsModified
                && !Equals(entry.Property(nameof(TestChangeReview.State)).OriginalValue, entry.Entity.State);
            var originalState = entry.Property(nameof(TestChangeReview.State)).OriginalValue is TestChangeReviewState state
                ? state
                : entry.Entity.State;
            var historicalAdvance = originalState == TestChangeReviewState.Approved
                && entry.Entity.State == TestChangeReviewState.Superseded;
            var remainsEligible = entry.Entity.State != TestChangeReviewState.Superseded
                && originalState != TestChangeReviewState.Superseded;
            var identityChanged = new[] { "ProjectId", "ReleaseId", "Discipline", "ArtifactKind", "BaseNumber", "Revision", "ChangeRequestId" }
                .Any(name => entry.Property(name).IsModified);
            if ((stateChanged && !historicalAdvance && !remainsEligible) || identityChanged)
                throw new DomainException("A Case package that supplies a Procedure assessment origin may only advance Approved to Superseded.");
        }
    }

    /// <summary>
    /// SQLite and other application callers receive the same source-side protection as PostgreSQL: after a
    /// Procedure package names an exact Case change, its child identity and project/build/discipline cannot be
    /// rewritten. The source package may make the one historical transition Approved -> Superseded.
    /// </summary>
    private async Task ValidateReferencedCaseChangesAsync(CancellationToken ct)
    {
        var changedChildren = _db.ChangeTracker.Entries<TestProcedureChange>()
            .Where(x => x.State is EntityState.Modified or EntityState.Deleted)
            .Where(x => x.State == EntityState.Deleted ||
                new[] { "TestChangeReviewId", "BaseNumber", "Revision" }
                    .Any(name => x.Property(name).IsModified))
            .Select(x => x.Entity.Id)
            .Where(x => x != Guid.Empty)
            .ToHashSet();
        var changedParents = _db.ChangeTracker.Entries<TestChangeReview>()
            .Where(x => x.State is EntityState.Modified or EntityState.Deleted)
            .Where(x => x.State == EntityState.Deleted ||
                new[] { "ProjectId", "ReleaseId", "Discipline", "ArtifactKind", "BaseNumber", "Revision", "ChangeRequestId", "State" }
                    .Any(name => x.Property(name).IsModified))
            .ToList();
        var parentIds = changedParents.Select(x => x.Entity.Id).Where(x => x != Guid.Empty).ToHashSet();
        if (parentIds.Count != 0)
        {
            var childIds = await _db.Set<TestProcedureChange>().AsNoTracking()
                .Where(x => parentIds.Contains(x.TestChangeReviewId))
                .Select(x => x.Id)
                .ToListAsync(ct);
            changedChildren.UnionWith(childIds);
        }
        if (changedChildren.Count == 0) return;

        var persistedReferences = await _db.TestChangeReviews.AsNoTracking()
            .Where(x => x.ArtifactKind == VerificationArtifactKind.Procedure
                && x.OriginKind == TestChangeReviewOriginKind.CaseChange
                && changedChildren.Contains(x.OriginReferenceId))
            .Select(x => x.OriginReferenceId)
            .ToListAsync(ct);
        var trackedReferences = _db.ChangeTracker.Entries<TestChangeReview>()
            .Where(x => x.State != EntityState.Deleted
                && x.Entity.ArtifactKind == VerificationArtifactKind.Procedure
                && x.Entity.OriginKind == TestChangeReviewOriginKind.CaseChange
                && changedChildren.Contains(x.Entity.OriginReferenceId))
            .Select(x => x.Entity.OriginReferenceId);
        var referencedIds = persistedReferences.Concat(trackedReferences).ToHashSet();
        if (referencedIds.Count == 0) return;

        var referencedSourceParentIds = (await _db.Set<TestProcedureChange>().AsNoTracking()
                .Where(x => referencedIds.Contains(x.Id))
                .Select(x => x.TestChangeReviewId)
                .ToListAsync(ct))
            .Concat(_db.ChangeTracker.Entries<TestProcedureChange>()
                .Where(x => referencedIds.Contains(x.Entity.Id))
                .Select(x => x.Entity.TestChangeReviewId))
            .ToHashSet();
        foreach (var entry in changedParents.Where(x => referencedSourceParentIds.Contains(x.Entity.Id)))
        {
            if (entry.State == EntityState.Deleted)
                throw new DomainException("A Case package that supplies a Procedure origin cannot be deleted.");
            var stateChanged = entry.Property(nameof(TestChangeReview.State)).IsModified
                && !Equals(entry.Property(nameof(TestChangeReview.State)).OriginalValue, entry.Entity.State);
            var historicalAdvance = Equals(entry.Property(nameof(TestChangeReview.State)).OriginalValue, TestChangeReviewState.Approved)
                && entry.Entity.State == TestChangeReviewState.Superseded;
            var identityChanged = new[] { "ProjectId", "ReleaseId", "Discipline", "ArtifactKind", "BaseNumber", "Revision", "ChangeRequestId" }
                .Any(name => entry.Property(name).IsModified);
            if ((stateChanged && !historicalAdvance) || identityChanged)
                throw new DomainException("A Case package that supplies a Procedure origin may only advance Approved to Superseded.");
        }

        foreach (var entry in _db.ChangeTracker.Entries<TestProcedureChange>()
                     .Where(x => changedChildren.Contains(x.Entity.Id) && referencedIds.Contains(x.Entity.Id)))
        {
            if (entry.State == EntityState.Deleted || new[] { "TestChangeReviewId", "BaseNumber", "Revision" }
                    .Any(name => entry.Property(name).IsModified))
                throw new DomainException("A Case change identity referenced by a Procedure package is immutable.");
        }
    }

    /// <summary>
    /// Save-boundary enforcement for newly materialized controlled Requirement
    /// revisions. The aggregate/API/materializer all make the same decision, but
    /// the database context is the last route a direct caller can take.
    /// </summary>
    private async Task ValidateRequirementParentSelectionsAsync(CancellationToken ct)
    {
        var addedIds = _db.ChangeTracker.Entries<RequirementRevision>()
            .Where(x => x.State == EntityState.Added
                && x.Entity.State == RequirementRevisionState.Active)
            .Select(x => x.Entity.Id).ToHashSet();
        var changedTraceEntries = _db.ChangeTracker.Entries<RequirementTraceLink>()
            .Where(x => x.State != EntityState.Unchanged).ToList();
        var changedLinkSourceIds = new HashSet<Guid>();
        foreach (var entry in changedTraceEntries)
        {
            // Any lifecycle-attached trace is #709 projection evidence, never an authored parent selection.
            // Its current Closed state affects baseline-aware read projections, but it must not be folded into
            // the immutable ParentRevisionIdsJson or treated as a direct parent edit at this save boundary.
            if (entry.Entity.ExactLinkSuspectLifecycleId is not null
                || (entry.State != EntityState.Added
                    && entry.Property(x => x.ExactLinkSuspectLifecycleId).OriginalValue is not null))
                continue;
            var wasAllocated = entry.State != EntityState.Added
                && entry.Property(x => x.Type).OriginalValue == RequirementTraceType.AllocatedFrom;
            if (entry.Entity.Type == RequirementTraceType.AllocatedFrom || wasAllocated)
                changedLinkSourceIds.Add(entry.Entity.SourceRevisionId);
        }
        var candidateIds = addedIds.Concat(changedLinkSourceIds).Distinct().ToList();
        if (candidateIds.Count == 0) return;

        var revisions = await _db.RequirementRevisions.AsNoTracking()
            .Where(x => candidateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var tracked in _db.ChangeTracker.Entries<RequirementRevision>()
                     .Where(x => x.State != EntityState.Deleted && candidateIds.Contains(x.Entity.Id)))
            revisions[tracked.Entity.Id] = tracked.Entity;
        var candidates = revisions.Values
            .Where(x => x.State != RequirementRevisionState.Retired)
            .ToList();
        if (candidates.Count == 0) return;
        var sourceChangeRequestIds = candidates.Where(x => x.SourceChangeRequestId.HasValue)
            .Select(x => x.SourceChangeRequestId!.Value).Distinct().ToList();
        var legacySourceChangeRequestIds = (await _db.SystemChangeRequests.AsNoTracking()
                .Where(x => sourceChangeRequestIds.Contains(x.Id)
                    && x.SnapshotContractVersion < SystemChangeRequest.CurrentSnapshotContractVersion)
                .Select(x => x.Id).ToListAsync(ct))
            .ToHashSet();
        var legacyRevisionIds = candidates
            .Where(x => x.SourceChangeRequestId.HasValue
                && legacySourceChangeRequestIds.Contains(x.SourceChangeRequestId.Value))
            .Select(x => x.Id).ToHashSet();

        var artifactIds = candidates.Select(x => x.ArtifactId).Distinct().ToList();
        var artifacts = await _db.Requirements.AsNoTracking()
            .Where(x => artifactIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var tracked in _db.ChangeTracker.Entries<RequirementArtifact>()
                     .Where(x => x.State != EntityState.Deleted && artifactIds.Contains(x.Entity.Id)))
            artifacts[tracked.Entity.Id] = tracked.Entity;
        if (artifacts.Count != artifactIds.Count)
            throw new DomainException("A requirement revision must name a persisted requirement artifact.");

        var projectIds = artifacts.Values.Select(x => x.ProjectId).Distinct().ToList();
        var configurations = await _db.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .AsNoTracking().Where(x => projectIds.Contains(x.ProjectId)).ToListAsync(ct);
        var policies = configurations.ToDictionary(x => x.ProjectId,
            x => ProjectLadderPolicyStorage.ResolvePersisted(x, x.ProjectId));
        foreach (var tracked in _db.ChangeTracker.Entries<ProjectLadderConfiguration>()
                     .Where(x => x.State != EntityState.Deleted && projectIds.Contains(x.Entity.ProjectId)))
            policies[tracked.Entity.ProjectId] = ProjectLadderPolicyStorage.ResolvePersisted(tracked.Entity, tracked.Entity.ProjectId);
        foreach (var projectId in projectIds.Where(x => !policies.ContainsKey(x)))
            policies[projectId] = LegacyLadderPolicy.Instance;
        if (_db.SaveBoundaryPolicyOverride is not null)
            foreach (var projectId in projectIds)
                policies[projectId] = _db.SaveBoundaryPolicyOverride;

        // Reconcile all exact authored links and parent scope in bounded set-based queries. The showcase
        // materializes more than a thousand requirement revisions in one unit of work; doing these reads once
        // per candidate turns the save boundary into thousands of serial database round trips and makes the
        // seed endpoint time out without changing the invariant being enforced.
        var linksBySource = await FinalRequirementParentLinksAsync(candidateIds, ct);
        var allParentIds = linksBySource.Values.SelectMany(x => x).Distinct().ToHashSet();
        var parentScopes = (await (from parent in _db.RequirementRevisions.AsNoTracking()
                                   join parentArtifact in _db.Requirements.AsNoTracking()
                                       on parent.ArtifactId equals parentArtifact.Id
                                   where allParentIds.Contains(parent.Id)
                                   select new RequirementScope(parent.Id, parentArtifact.ProjectId,
                                       parentArtifact.Level, parentArtifact.BaseNumber, parent.EffectiveBaselineId))
            .ToListAsync(ct)).ToDictionary(x => x.RevisionId);
        var trackedParentRevisions = _db.ChangeTracker.Entries<RequirementRevision>()
            .Where(x => x.State != EntityState.Deleted && allParentIds.Contains(x.Entity.Id))
            .ToList();
        var trackedParentArtifactIds = trackedParentRevisions.Select(x => x.Entity.ArtifactId).Distinct().ToList();
        var parentArtifacts = await _db.Requirements.AsNoTracking()
            .Where(x => trackedParentArtifactIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var tracked in _db.ChangeTracker.Entries<RequirementArtifact>()
                     .Where(x => x.State != EntityState.Deleted && trackedParentArtifactIds.Contains(x.Entity.Id)))
            parentArtifacts[tracked.Entity.Id] = tracked.Entity;
        foreach (var tracked in trackedParentRevisions)
        {
            if (parentArtifacts.TryGetValue(tracked.Entity.ArtifactId, out var parentArtifact))
                parentScopes[tracked.Entity.Id] = new RequirementScope(tracked.Entity.Id,
                    parentArtifact.ProjectId, parentArtifact.Level, parentArtifact.BaseNumber,
                    tracked.Entity.EffectiveBaselineId);
        }
        var activeParentIds = (await _db.RequirementRevisions.AsNoTracking()
                .Where(x => allParentIds.Contains(x.Id) && x.State == RequirementRevisionState.Active)
                .Select(x => x.Id).ToListAsync(ct))
            .ToHashSet();
        foreach (var tracked in trackedParentRevisions)
        {
            if (tracked.Entity.State == RequirementRevisionState.Active) activeParentIds.Add(tracked.Entity.Id);
            else activeParentIds.Remove(tracked.Entity.Id);
        }
        var governedBaselineIds = candidates.Select(x => x.EffectiveBaselineId).Distinct().ToList();
        var baselineMemberships = (await _db.BaselineRequirements.AsNoTracking()
                .Where(x => governedBaselineIds.Contains(x.BaselineId) && allParentIds.Contains(x.RevisionId))
                .Select(x => new { x.BaselineId, x.RevisionId }).ToListAsync(ct))
            .Select(x => (x.BaselineId, x.RevisionId)).ToHashSet();
        foreach (var entry in _db.ChangeTracker.Entries<BaselineRequirementSelection>()
                     .Where(x => governedBaselineIds.Contains(x.Entity.BaselineId)
                         && allParentIds.Contains(x.Entity.RevisionId)))
        {
            var key = (entry.Entity.BaselineId, entry.Entity.RevisionId);
            if (entry.State == EntityState.Deleted) baselineMemberships.Remove(key);
            else if (entry.State == EntityState.Added) baselineMemberships.Add(key);
        }

        foreach (var revision in candidates)
        {
            var artifact = artifacts[revision.ArtifactId];
            if (!policies.TryGetValue(artifact.ProjectId, out var policy))
                throw new DomainException($"Project {artifact.ProjectId} has no persisted ladder configuration.");
            IReadOnlyList<RequirementLevel> parentLevels;
            try
            {
                try
                {
                    _ = policy.Definition(artifact.Level);
                    parentLevels = policy.ParentLevels(artifact.Level);
                }
                catch (DomainException) when (policy is ILegacyLadderCompatibilityPolicy
                    && artifact.Level is RequirementLevel.Customer or RequirementLevel.Interface)
                {
                    // Stored legacy projects persist only their authored System/HLR/LLR graph. Customer
                    // imports and Interface controls remain the code-owned legacy catalogue levels, so use
                    // the established compatibility definition for those known levels. Unknown configured
                    // levels still fail closed below; they are never inferred as roots.
                    _ = LegacyLadderPolicy.Instance.Definition(artifact.Level);
                    parentLevels = LegacyLadderPolicy.Instance.ParentLevels(artifact.Level);
                }
            }
            catch (Exception ex) when (ex is DomainException or InvalidOperationException or KeyNotFoundException)
            {
                throw new DomainException(
                    $"Requirement {artifact.BaseNumber} has an unknown configured upstream topology.");
            }
            if (_db.AllowLegacyHistoricalSeed
                && revision.ParentKind == RequirementParentKind.Unspecified
                && legacyRevisionIds.Contains(revision.Id)
                && addedIds.Contains(revision.Id)
                && !changedLinkSourceIds.Contains(revision.Id))
                continue;
            var linkIds = linksBySource[revision.Id];
            if (parentLevels.Count == 0)
            {
                if (linkIds.Count != 0)
                    throw new DomainException($"{artifact.BaseNumber} is a configured root and cannot carry exact upstream links.");
                continue;
            }
            if (revision.ParentKind == RequirementParentKind.Unspecified)
            {
                if (addedIds.Contains(revision.Id) || changedLinkSourceIds.Contains(revision.Id))
                    throw new DomainException(
                        $"{artifact.BaseNumber}.{revision.Revision:D2} must resolve Allocated or Derived exact parents before it is persisted or its exact links are changed.");
                // A historical, untouched Unspecified row remains legacy
                // evidence. It should not normally be a candidate, but keep
                // this branch fail-closed if a future caller expands the set.
                continue;
            }
            var ids = revision.ParentRevisionIds;
            ExactParentSelectionPolicy.Validate(
                revision.ParentKind switch
                {
                    RequirementParentKind.Allocated => ExactParentClassification.Allocated,
                    RequirementParentKind.Derived => ExactParentClassification.Derived,
                    _ => ExactParentClassification.Unspecified
                }, ids, revision.DerivedRationale, "requirement revision");
            if (!linkIds.SetEquals(ids))
            {
                throw new DomainException(
                    $"{artifact.BaseNumber}.{revision.Revision:D2} exact parent identities do not match its persisted AllocatedFrom links.");
            }

            foreach (var id in ids)
            {
                if (!parentScopes.TryGetValue(id, out var parent))
                    throw new DomainException($"{artifact.BaseNumber} names a requirement revision that is not persisted.");
                if (parent.ProjectId != artifact.ProjectId
                    || !parentLevels.Contains(parent.Level)
                    || !baselineMemberships.Contains((revision.EffectiveBaselineId, id)))
                    throw new DomainException(
                        $"{artifact.BaseNumber}.{revision.Revision:D2} exact parents must be valid configured levels in the same project and governed baseline.");
                if (!activeParentIds.Contains(id))
                    throw new DomainException(
                        $"{artifact.BaseNumber}.{revision.Revision:D2} names a stale or non-current requirement revision.");
            }
        }
    }

    private async Task<Dictionary<Guid, HashSet<Guid>>> FinalRequirementParentLinksAsync(
        IReadOnlyCollection<Guid> sourceRevisionIds, CancellationToken ct)
    {
        var sourceIds = sourceRevisionIds.Distinct().ToHashSet();
        var links = await _db.RequirementTraces.AsNoTracking()
            .Where(x => sourceIds.Contains(x.SourceRevisionId) && x.Type == RequirementTraceType.AllocatedFrom)
            // A lifecycle-attached relation records #709 downstream revalidation evidence, not a new authored
            // parent selection. Closed lifecycle state is included by baseline-aware read projections only.
            .Where(x => x.ExactLinkSuspectLifecycleId == null)
            .Select(x => new { x.SourceRevisionId, x.TargetRevisionId }).ToListAsync(ct);
        var linksBySource = sourceIds.ToDictionary(x => x, _ => new HashSet<Guid>());
        foreach (var link in links)
            linksBySource[link.SourceRevisionId].Add(link.TargetRevisionId);
        foreach (var entry in _db.ChangeTracker.Entries<RequirementTraceLink>()
                     .Where(x => sourceIds.Contains(x.Entity.SourceRevisionId)))
        {
            var linkIds = linksBySource[entry.Entity.SourceRevisionId];
            var currentLifecycleIsAuthored = entry.Entity.ExactLinkSuspectLifecycleId is null;
            var originalLifecycleIsAuthored = entry.State == EntityState.Added
                || entry.Property(x => x.ExactLinkSuspectLifecycleId).OriginalValue is null;
            if (entry.State is EntityState.Deleted or EntityState.Modified && originalLifecycleIsAuthored)
                linkIds.Remove(entry.Entity.TargetRevisionId);
            if (entry.State != EntityState.Deleted
                && entry.Entity.Type == RequirementTraceType.AllocatedFrom
                && currentLifecycleIsAuthored
                && !linkIds.Contains(entry.Entity.TargetRevisionId))
                linkIds.Add(entry.Entity.TargetRevisionId);
        }
        return linksBySource;
    }

    /// <summary>
    /// A dormant software Procedure is one immutable identity plus its first classified revision. Do not
    /// allow a direct EF caller to insert the header alone and attach a revision later: that creates a window
    /// where the identity exists without the allocation/derived decision that gives it meaning.
    /// </summary>
    private Task ValidateProcedureHeadersAsync(CancellationToken ct)
    {
        _ = ct;
        var addedSoftwareProcedures = _db.ChangeTracker.Entries<TestProcedure>()
            .Where(x => x.State == EntityState.Added
                && x.Entity.ArtifactKind == VerificationArtifactKind.Procedure
                && x.Entity.Level != TestProcedureLevel.System)
            .Select(x => x.Entity.Id)
            .ToHashSet();
        if (addedSoftwareProcedures.Count == 0) return Task.CompletedTask;

        var initialRevisionOwners = _db.ChangeTracker.Entries<TestProcedureRevision>()
            .Where(x => x.State == EntityState.Added
                && addedSoftwareProcedures.Contains(x.Entity.ProcedureId)
                && x.Entity.Revision == 0
                && x.Entity.State == TestProcedureState.Draft)
            .Select(x => x.Entity.ProcedureId)
            .ToHashSet();
        // The governed #726 cutover is the narrow attributed exception: a migration-generated software
        // Procedure header is saved with its Approved, Allocated, migration-authored revision(s) in the same
        // unit of work. Authoring paths still require the Draft-0 initial revision above.
        var migrationOwners = _db.ChangeTracker.Entries<TestProcedureRevision>()
            .Where(x => x.State == EntityState.Added
                && addedSoftwareProcedures.Contains(x.Entity.ProcedureId)
                && x.Entity.AuthorId == VerificationArtifactProfileSchema.GovernedMigrationActor
                // #726: a retired Case revision is mirrored as a Retired migration Procedure revision so
                // historical numbering/provenance is preserved without manufacturing an active claim; the
                // migration remains the attributed header authority for both states.
                && (x.Entity.State == TestProcedureState.Approved
                    || x.Entity.State == TestProcedureState.Retired)
                && x.Entity.ParentKind == VerificationProcedureParentKind.Allocated)
            .Select(x => x.Entity.ProcedureId)
            .ToHashSet();
        // A controlled TCR materializer is the other attributed authority: it writes the Approved revision 0
        // with a source test change request and an explicit Allocated/Derived classification on the package's
        // authority, so the header never exists without its classified first revision.
        var materializerOwners = _db.ChangeTracker.Entries<TestProcedureRevision>()
            .Where(x => x.State == EntityState.Added
                && addedSoftwareProcedures.Contains(x.Entity.ProcedureId)
                && x.Entity.Revision == 0
                && x.Entity.State == TestProcedureState.Approved
                && x.Entity.SourceTestChangeRequestId != null
                && x.Entity.ParentKind != VerificationProcedureParentKind.Unspecified)
            .Select(x => x.Entity.ProcedureId)
            .ToHashSet();
        var missing = addedSoftwareProcedures.Except(initialRevisionOwners)
            .Except(migrationOwners).Except(materializerOwners).ToArray();
        if (missing.Length > 0)
            throw new DomainException("A software Procedure header must be saved with its initial revision in the same unit of work.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Refuses a coverage link between a procedure and a requirement at a different level.
    ///
    /// An HLR test procedure exists because it verifies one or more HLRs. Linking one to a System requirement
    /// is not a thing a verification engineer means to do, and the product allowed it — which is how a System
    /// change request came to raise work in the HLR queue: retiring the System requirement stranded the HLR
    /// procedure, and the orphan was routed by the procedure's level onto a System change request. Forbidding
    /// the link removes that whole class of problem rather than routing around it, because a retirement can
    /// then only ever strand procedures of its own level.
    ///
    /// Enforced here rather than in the constructor because this is the one place every write passes through.
    /// The rule was already true of all 1,251 links in the demo database, so this fixes nothing retroactively —
    /// it stops the next one, including from code not yet written.
    /// </summary>
    private async Task RefuseCrossLevelCoverageAsync(CancellationToken ct)
    {
        var added = _db.ChangeTracker.Entries<TestRequirementCoverage>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .ToList();
        if (added.Count == 0) return;

        var procedureRevisionIds = added.Select(x => x.ProcedureRevisionId).Distinct().ToList();
        var requirementRevisionIds = added.Select(x => x.RequirementRevisionId).Distinct().ToList();

        var procedureLevels = await (from revision in _db.TestProcedureRevisions.AsNoTracking()
                                     join procedure in _db.TestProcedures.AsNoTracking()
                                         on revision.ProcedureId equals procedure.Id
                                     where procedureRevisionIds.Contains(revision.Id)
                                     select new ProcedureScope(revision.Id, revision.ProcedureId, revision.Revision,
                                         revision.EffectiveBaselineId, procedure.ProjectId, procedure.Level,
                                         procedure.ArtifactKind, procedure.BaseNumber, revision.State,
                                         revision.AuthorId, revision.ParentKind))
            .ToDictionaryAsync(x => x.RevisionId, ct);
        var trackedProcedureOwners = _db.ChangeTracker.Entries<TestProcedure>()
            .Where(x => x.State != EntityState.Deleted)
            .ToDictionary(x => x.Entity.Id, x => x.Entity);
        var changedProcedureRevisions = _db.ChangeTracker.Entries<TestProcedureRevision>()
            .Where(x => x.State != EntityState.Deleted && procedureRevisionIds.Contains(x.Entity.Id))
            .ToList();
        var missingProcedureOwnerIds = changedProcedureRevisions
            .Select(x => x.Entity.ProcedureId).Distinct()
            .Where(x => !trackedProcedureOwners.Values.Any(owner => owner.Id == x))
            .ToList();
        var persistedProcedureOwners = await _db.TestProcedures.AsNoTracking()
            .Where(x => missingProcedureOwnerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var revisionEntry in changedProcedureRevisions)
        {
            var owner = trackedProcedureOwners.TryGetValue(revisionEntry.Entity.ProcedureId, out var trackedOwner)
                ? trackedOwner
                : persistedProcedureOwners.GetValueOrDefault(revisionEntry.Entity.ProcedureId);
            if (owner is not null)
                procedureLevels[revisionEntry.Entity.Id] = new ProcedureScope(revisionEntry.Entity.Id,
                    revisionEntry.Entity.ProcedureId, revisionEntry.Entity.Revision,
                    revisionEntry.Entity.EffectiveBaselineId, owner.ProjectId, owner.Level, owner.ArtifactKind,
                    owner.BaseNumber, revisionEntry.Entity.State,
                    revisionEntry.Entity.AuthorId, revisionEntry.Entity.ParentKind);
        }
        var requirementLevels = await (from revision in _db.RequirementRevisions.AsNoTracking()
                                       join artifact in _db.Requirements.AsNoTracking()
                                           on revision.ArtifactId equals artifact.Id
                                       where requirementRevisionIds.Contains(revision.Id)
                                       select new RequirementScope(revision.Id, artifact.ProjectId, artifact.Level, artifact.BaseNumber, revision.EffectiveBaselineId))
            .ToDictionaryAsync(x => x.RevisionId, ct);
        var trackedRequirementArtifacts = _db.ChangeTracker.Entries<RequirementArtifact>()
            .Where(x => x.State != EntityState.Deleted)
            .ToDictionary(x => x.Entity.Id, x => x.Entity);
        var changedRequirementRevisions = _db.ChangeTracker.Entries<RequirementRevision>()
            .Where(x => x.State != EntityState.Deleted && requirementRevisionIds.Contains(x.Entity.Id))
            .ToList();
        var missingRequirementArtifactIds = changedRequirementRevisions
            .Select(x => x.Entity.ArtifactId).Distinct()
            .Where(x => !trackedRequirementArtifacts.ContainsKey(x))
            .ToList();
        var persistedRequirementArtifacts = await _db.Requirements.AsNoTracking()
            .Where(x => missingRequirementArtifactIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var revisionEntry in changedRequirementRevisions)
        {
            var artifact = trackedRequirementArtifacts.GetValueOrDefault(revisionEntry.Entity.ArtifactId)
                ?? persistedRequirementArtifacts.GetValueOrDefault(revisionEntry.Entity.ArtifactId);
            if (artifact is not null)
                requirementLevels[revisionEntry.Entity.Id] = new RequirementScope(revisionEntry.Entity.Id,
                    artifact.ProjectId, artifact.Level, artifact.BaseNumber, revisionEntry.Entity.EffectiveBaselineId);
        }

        var projectIds = procedureLevels.Values.Select(x => x.ProjectId)
            .Concat(requirementLevels.Values.Select(x => x.ProjectId)).Distinct().ToList();
        var configurations = await _db.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .AsNoTracking().Where(x => projectIds.Contains(x.ProjectId)).ToListAsync(ct);
        var policies = configurations.ToDictionary(x => x.ProjectId,
            x => ProjectLadderPolicyStorage.ResolvePersisted(x, x.ProjectId));
        foreach (var tracked in _db.ChangeTracker.Entries<ProjectLadderConfiguration>()
                     .Where(x => x.State != EntityState.Deleted && projectIds.Contains(x.Entity.ProjectId)))
            policies[tracked.Entity.ProjectId] = ProjectLadderPolicyStorage.ResolvePersisted(tracked.Entity, tracked.Entity.ProjectId);
        foreach (var projectId in projectIds.Where(x => !policies.ContainsKey(x)))
            policies[projectId] = LegacyLadderPolicy.Instance;
        if (_db.SaveBoundaryPolicyOverride is not null)
            foreach (var projectId in projectIds)
                policies[projectId] = _db.SaveBoundaryPolicyOverride;

        foreach (var link in added)
        {
            // A revision not yet in the database is one being written in this same unit of work. Its level is
            // not knowable here, and refusing on that basis would block legitimate writes; the authoring paths
            // check before proposing.
            if (!procedureLevels.TryGetValue(link.ProcedureRevisionId, out var procedure)
                || !requirementLevels.TryGetValue(link.RequirementRevisionId, out var requirement))
                throw new DomainException("A verification coverage link must name persisted revisions in the same unit of work.");
            if (procedure.ProjectId != requirement.ProjectId)
                throw new DomainException("A verification coverage link cannot cross projects.");
            if (procedure.ArtifactKind == VerificationArtifactKind.Procedure && procedure.Level != TestProcedureLevel.System)
                throw new DomainException("A software Procedure cannot link directly to Requirement coverage; link it to exact Case revisions instead.");
            if (!policies.TryGetValue(procedure.ProjectId, out var policyForProject))
                throw new DomainException($"Project {procedure.ProjectId} has no persisted ladder configuration.");
            if (SameLevel(policyForProject, procedure.Level, requirement.Level)) continue;
            var artifactNoun = procedure.Level == TestProcedureLevel.System ? "test procedure" : "test case";
            throw new DomainException(
                $"{procedure.BaseNumber} is a {procedure.Level} {artifactNoun} and cannot verify {requirement.BaseNumber}, which is a {requirement.Level} requirement. A {artifactNoun} covers requirements at its own level.");
        }
    }

    /// <summary>The one true correspondence between a procedure's level and a requirement's.</summary>
    private static bool SameLevel(ILadderPolicy policy, TestProcedureLevel procedure, RequirementLevel requirement) =>
        policy.RequirementLevelFor(procedure) == requirement;

    private sealed record ProcedureScope(Guid RevisionId, Guid ArtifactId, int Revision,
        Guid? EffectiveBaselineId, Guid ProjectId, TestProcedureLevel Level,
        VerificationArtifactKind ArtifactKind, string BaseNumber, TestProcedureState State,
        string AuthorId, VerificationProcedureParentKind ParentKind);

    private sealed record RequirementScope(Guid RevisionId, Guid ProjectId, RequirementLevel Level, string BaseNumber,
        Guid EffectiveBaselineId);

    /// <summary>Enforces the Procedure-side exact-parent-or-derived seam before a revision is persisted.</summary>
    private async Task ValidateProcedureParentsAsync(CancellationToken ct)
    {
        var revisionIds = _db.ChangeTracker.Entries<TestProcedureRevision>()
            .Where(x => x.State != EntityState.Unchanged)
            .Select(x => x.Entity.Id)
            .Concat(_db.ChangeTracker.Entries<TestCaseProcedureLink>()
                .Where(x => x.State != EntityState.Unchanged)
                .Select(x => x.Entity.ProcedureRevisionId))
            .Distinct().ToList();
        if (revisionIds.Count == 0) return;
        var revisions = await _db.TestProcedureRevisions.AsNoTracking()
            .Where(x => revisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var tracked in _db.ChangeTracker.Entries<TestProcedureRevision>()
                     .Where(x => x.State != EntityState.Deleted && revisionIds.Contains(x.Entity.Id)))
            revisions[tracked.Entity.Id] = tracked.Entity;
        var ownerIds = revisions.Values.Select(r => r.ProcedureId).Distinct().ToList();
        var owners = await _db.TestProcedures.AsNoTracking()
            .Where(x => ownerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var tracked in _db.ChangeTracker.Entries<TestProcedure>().Where(x => x.State != EntityState.Deleted
                     && ownerIds.Contains(x.Entity.Id)))
            owners[tracked.Entity.Id] = tracked.Entity;
        var parentIdsByRevision = await FinalParentIdsAsync(revisions.Keys.ToArray(), ct);
        foreach (var revision in revisions.Values)
        {
            if (!owners.TryGetValue(revision.ProcedureId, out var owner)) continue;
            // #726 migration exception: a migration-generated revision seeds environment/setup, ordered

            // steps and expected observations deterministically from the exact legacy Case, while test data,
            // cleanup and tooling are honestly empty (the legacy Case had no such content). Fabricating
            // text would be dishonest; ordinary authored Procedures still require the full vocabulary.
            var isGovernedMigration = revision.AuthorId == VerificationArtifactProfileSchema.GovernedMigrationActor
                && revision.ParentKind == VerificationProcedureParentKind.Allocated
                && revision.State == TestProcedureState.Approved;
            if (owner.ArtifactKind == VerificationArtifactKind.Procedure && owner.Level != TestProcedureLevel.System
                && revision.State != TestProcedureState.Retired
                && (string.IsNullOrWhiteSpace(revision.EnvironmentSetup)
                    || string.IsNullOrWhiteSpace(revision.OrderedSteps)
                    || string.IsNullOrWhiteSpace(revision.ExpectedObservations)
                    || !isGovernedMigration && (string.IsNullOrWhiteSpace(revision.TestData)
                        || string.IsNullOrWhiteSpace(revision.Cleanup)
                        || string.IsNullOrWhiteSpace(revision.ToolingAutomation))))
                throw new DomainException("A software Procedure revision requires environment/setup, test data, ordered steps, expected observations, cleanup, and tooling/automation.");
            // Retired revisions are exempt inside ValidateProcedureParents, so a historical migration mirror
            // preserved in the Retired state never needs a global persistence exemption.
            revision.ValidateProcedureParents(owner, parentIdsByRevision[revision.Id]);
        }
    }

    /// <summary>
    /// Persists the Case/System Procedure exact-parent decision together with the
    /// exact coverage rows it names. This is deliberately a save-boundary check:
    /// callers that bypass the review/API/materializer still cannot write a typed
    /// allocation whose links disagree with its immutable classification.
    /// Legacy Unspecified revisions are retained as historical evidence; every
    /// newly authored #738 revision carries an explicit mode before this check.
    /// </summary>
    private async Task ValidateCaseSystemParentSelectionsAsync(CancellationToken ct)
    {
        var changedCoverage = _db.ChangeTracker.Entries<TestRequirementCoverage>()
            .Where(x => x.State != EntityState.Unchanged)
            .ToList();
        var removedRequirementRevisionIds = _db.ChangeTracker.Entries<RequirementRevision>()
            .Where(x => x.State == EntityState.Deleted)
            .Select(x => x.Entity.Id)
            .ToHashSet();
        // #709 reopen/dematerialization replaces an authored non-suspect link with a suspect
        // carry-forward link in the same unit of work. That is lifecycle evidence, not a rewrite of
        // the immutable procedure revision's approved parent selection. Do not make the transient
        // zero-non-suspect state fail the XOR guard. Confirmation (a suspect row becoming non-suspect)
        // remains a candidate and is checked against the applicable target baseline below.
        var suspectLifecycleRevisionIds = changedCoverage
            .GroupBy(x => x.Entity.ProcedureRevisionId)
            .Where(group =>
                // A fallback link is suspect lifecycle evidence, while a link to a requirement revision
                // that the same reopen is deleting simply disappears. Both are dematerializer-owned
                // changes and must not be mistaken for an authored parent edit.
                group.Any(x => x.State != EntityState.Deleted
                    && (x.Entity.IsSuspect || IsConfirmedSuspectLifecycleEntry(x)))
                    && group.All(x => x.State == EntityState.Deleted
                        || x.Entity.IsSuspect || IsConfirmedSuspectLifecycleEntry(x))
                || group.All(x => x.State == EntityState.Deleted
                    && removedRequirementRevisionIds.Contains(x.Entity.RequirementRevisionId)))
            .Select(group => group.Key)
            .ToHashSet();
        var revisionIds = _db.ChangeTracker.Entries<TestProcedureRevision>()
            .Where(x => x.State != EntityState.Unchanged && x.State != EntityState.Deleted)
            .Select(x => x.Entity.Id)
            .Concat(changedCoverage
                .Where(x => !suspectLifecycleRevisionIds.Contains(x.Entity.ProcedureRevisionId))
            .Select(x => x.Entity.ProcedureRevisionId))
            .Distinct().ToList();
        if (revisionIds.Count == 0) return;
        var revisionIdSet = revisionIds.ToHashSet();
        var revisionEntryById = _db.ChangeTracker.Entries<TestProcedureRevision>()
            .Where(x => revisionIdSet.Contains(x.Entity.Id))
            .ToDictionary(x => x.Entity.Id);
        var addedRevisionIds = revisionEntryById.Values
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity.Id)
            .ToHashSet();
        var changedCoverageByRevision = changedCoverage
            .GroupBy(x => x.Entity.ProcedureRevisionId)
            .ToDictionary(x => x.Key, x => x.ToList());

        var revisions = await _db.TestProcedureRevisions.AsNoTracking()
            .Where(x => revisionIdSet.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var tracked in revisionEntryById.Values.Where(x => x.State != EntityState.Deleted))
            revisions[tracked.Entity.Id] = tracked.Entity;
        var ownerIds = revisions.Values.Select(x => x.ProcedureId).Distinct().ToList();
        var owners = await _db.TestProcedures.AsNoTracking()
            .Where(x => ownerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var tracked in _db.ChangeTracker.Entries<TestProcedure>()
                     .Where(x => x.State != EntityState.Deleted && ownerIds.Contains(x.Entity.Id)))
            owners[tracked.Entity.Id] = tracked.Entity;

        var changedCaseSystem = revisions.Values.Where(revision =>
            revision.State == TestProcedureState.Approved
            && owners.TryGetValue(revision.ProcedureId, out var owner)
            && (owner.Level == TestProcedureLevel.System
                || owner.ArtifactKind == VerificationArtifactKind.Case)).ToList();
        foreach (var revision in changedCaseSystem)
        {
            if (addedRevisionIds.Contains(revision.Id) && revision.SourceTestChangeRequestId is not null
                && revision.ParentKind == VerificationProcedureParentKind.Unspecified)
                throw new DomainException("A newly written active Case/System Procedure revision must resolve Allocated or Derived exact parents before it is persisted.");
        }
        // A persisted Unspecified row is tolerated only while it remains
        // untouched historical evidence.  Once a caller adds/removes a link
        // for it, the write is an alternate editing route and must resolve the
        // same XOR invariant as a newly added revision.
        var candidates = changedCaseSystem
            // Rows without a producing TCR are the documented legacy/import shape. They may be loaded as
            // historical evidence during fixture/import setup, but any later mutation of that persisted row
            // remains in candidates and is therefore checked below. Controlled materialization always has a
            // SourceTestChangeRequestId and cannot use this compatibility seam.
            .Where(revision => !(addedRevisionIds.Contains(revision.Id)
                && revision.SourceTestChangeRequestId is null
                && revision.ParentKind == VerificationProcedureParentKind.Unspecified))
            .ToList();
        if (candidates.Count == 0) return;

        var requirementIds = new HashSet<Guid>();
        var coverageByRevision = new Dictionary<Guid, IReadOnlyCollection<Guid>>();
        var persistedCoverage = await _db.TestCoverage.AsNoTracking()
            .Where(x => revisionIdSet.Contains(x.ProcedureRevisionId))
            .Select(x => new { x.ProcedureRevisionId, x.RequirementRevisionId, x.IsSuspect })
            .ToListAsync(ct);
        var persistedCoverageByRevision = persistedCoverage
            .GroupBy(x => x.ProcedureRevisionId)
            .ToDictionary(x => x.Key, x => x.ToList());
        foreach (var revision in candidates)
        {
            var revisionWasAdded = addedRevisionIds.Contains(revision.Id);
            var ids = persistedCoverageByRevision.TryGetValue(revision.Id, out var persisted)
                ? persisted.Where(x => revisionWasAdded || !x.IsSuspect)
                    .Select(x => x.RequirementRevisionId).ToHashSet()
                : [];
            if (changedCoverageByRevision.TryGetValue(revision.Id, out var changedForRevision))
            {
                foreach (var entry in changedForRevision)
                {
                    if (entry.State == EntityState.Deleted) ids.Remove(entry.Entity.RequirementRevisionId);
                    else if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
                    {
                        if (entry.Entity.IsSuspect) ids.Remove(entry.Entity.RequirementRevisionId);
                        else ids.Add(entry.Entity.RequirementRevisionId);
                    }
                }
            }
            coverageByRevision[revision.Id] = ids;
            requirementIds.UnionWith(ids);
        }

        // Keep the typed projection below separate from _db.TestCoverage: it needs
        // the requirement's project/level/baseline, not the coverage row itself.
        var requirementIdSet = requirementIds.ToHashSet();
        var requirementScopes = await (from revision in _db.RequirementRevisions.AsNoTracking()
                                       join artifact in _db.Requirements.AsNoTracking()
                                           on revision.ArtifactId equals artifact.Id
                                       where requirementIdSet.Contains(revision.Id)
                                       select new RequirementScope(revision.Id, artifact.ProjectId,
                                           artifact.Level, artifact.BaseNumber, revision.EffectiveBaselineId))
            .ToDictionaryAsync(x => x.RevisionId, ct);
        var trackedRequirementArtifacts = _db.ChangeTracker.Entries<RequirementArtifact>()
            .Where(x => x.State != EntityState.Deleted)
            .ToDictionary(x => x.Entity.Id, x => x.Entity);
        var changedRequirementRevisions = _db.ChangeTracker.Entries<RequirementRevision>()
            .Where(x => x.State != EntityState.Deleted && requirementIdSet.Contains(x.Entity.Id))
            .ToList();
        var missingRequirementArtifactIds = changedRequirementRevisions
            .Select(x => x.Entity.ArtifactId).Distinct()
            .Where(x => !trackedRequirementArtifacts.ContainsKey(x))
            .ToList();
        var persistedRequirementArtifacts = await _db.Requirements.AsNoTracking()
            .Where(x => missingRequirementArtifactIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var tracked in changedRequirementRevisions)
        {
            var artifact = trackedRequirementArtifacts.GetValueOrDefault(tracked.Entity.ArtifactId)
                ?? persistedRequirementArtifacts.GetValueOrDefault(tracked.Entity.ArtifactId);
            if (artifact is not null)
                requirementScopes[tracked.Entity.Id] = new RequirementScope(tracked.Entity.Id, artifact.ProjectId,
                    artifact.Level, artifact.BaseNumber, tracked.Entity.EffectiveBaselineId);
        }
        var governedBaselineIds = candidates.Where(x => x.EffectiveBaselineId.HasValue)
            .Select(x => x.EffectiveBaselineId!.Value).Distinct().ToList();
        // A carried verification revision can remain immutable at the baseline that created it while a
        // successor requirement revision is selected in the child baseline being materialized. Keep both
        // exact memberships available: direct authored scope must be in the Procedure baseline, while the
        // narrowly-recognized carried case below may use the descendant requirement baseline.
        var requirementBaselineIds = requirementScopes.Values
            .Select(x => x.EffectiveBaselineId).Distinct().ToList();
        var allBaselineIds = governedBaselineIds.Concat(requirementBaselineIds).Distinct().ToList();
        var baselineScopes = (await _db.CandidateBaselines.AsNoTracking()
            .Where(x => allBaselineIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ProjectId, x.ReleaseId })
            .ToListAsync(ct))
            .ToDictionary(x => x.Id, x => (ProjectId: x.ProjectId, ReleaseId: x.ReleaseId));
        foreach (var tracked in _db.ChangeTracker.Entries<CandidateBaseline>()
                     .Where(x => x.State != EntityState.Deleted && allBaselineIds.Contains(x.Entity.Id)))
            baselineScopes[tracked.Entity.Id] = (tracked.Entity.ProjectId, tracked.Entity.ReleaseId);
        var baselineMemberships = (await _db.BaselineRequirements.AsNoTracking()
                .Where(x => allBaselineIds.Contains(x.BaselineId) && requirementIds.Contains(x.RevisionId))
                .Select(x => new { x.BaselineId, x.RevisionId }).ToListAsync(ct))
            .Select(x => (x.BaselineId, x.RevisionId)).ToHashSet();
        foreach (var entry in _db.ChangeTracker.Entries<BaselineRequirementSelection>()
                     .Where(x => allBaselineIds.Contains(x.Entity.BaselineId)
                         && requirementIds.Contains(x.Entity.RevisionId)))
        {
            var key = (entry.Entity.BaselineId, entry.Entity.RevisionId);
            if (entry.State == EntityState.Deleted) baselineMemberships.Remove(key);
            else if (entry.State == EntityState.Added) baselineMemberships.Add(key);
        }

        var projectIds = candidates.Select(x => owners[x.ProcedureId].ProjectId).Distinct().ToList();
        var configurations = await _db.ProjectLadderConfigurations
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .AsNoTracking().Where(x => projectIds.Contains(x.ProjectId)).ToListAsync(ct);
        var policies = configurations.ToDictionary(x => x.ProjectId,
            x => ProjectLadderPolicyStorage.ResolvePersisted(x, x.ProjectId));
        foreach (var tracked in _db.ChangeTracker.Entries<ProjectLadderConfiguration>()
                     .Where(x => x.State != EntityState.Deleted && projectIds.Contains(x.Entity.ProjectId)))
            policies[tracked.Entity.ProjectId] = ProjectLadderPolicyStorage.ResolvePersisted(tracked.Entity, tracked.Entity.ProjectId);
        foreach (var projectId in projectIds.Where(x => !policies.ContainsKey(x)))
            policies[projectId] = LegacyLadderPolicy.Instance;
        if (_db.SaveBoundaryPolicyOverride is not null)
            foreach (var projectId in projectIds)
                policies[projectId] = _db.SaveBoundaryPolicyOverride;

        foreach (var revision in candidates)
        {
            var owner = owners[revision.ProcedureId];
            var ids = coverageByRevision[revision.Id];
            var noun = owner.Level == TestProcedureLevel.System ? "System Procedure" : "software Case";
            var revisionWasAdded = addedRevisionIds.Contains(revision.Id);
            if (!revisionWasAdded && revision.State == TestProcedureState.Approved)
            {
                var changedForRevision = changedCoverageByRevision.GetValueOrDefault(revision.Id) ?? [];
                foreach (var entry in changedForRevision.Where(x => x.State == EntityState.Added && !x.Entity.IsSuspect
                             && !IsConfirmedSuspectLifecycleEntry(x)))
                {
                    throw new DomainException(
                        $"Adding an exact parent to an existing approved {noun} revision requires a controlled successor with a signed parent selection.");
                }
                foreach (var entry in changedForRevision.Where(x => x.State == EntityState.Deleted && !x.Entity.IsSuspect))
                    throw new DomainException(
                        $"Removing an exact parent from an existing approved {noun} revision requires a controlled successor with a signed parent selection.");
            }
            ExactParentSelectionPolicy.Validate(
                revision.ParentKind == VerificationProcedureParentKind.Derived
                    ? ExactParentClassification.Derived
                    : revision.ParentKind == VerificationProcedureParentKind.Allocated
                        ? ExactParentClassification.Allocated
                        : ExactParentClassification.Unspecified,
                ids, revision.DerivedRationale, noun);
            if (!policies.TryGetValue(owner.ProjectId, out var policy))
                throw new DomainException($"Project {owner.ProjectId} has no persisted ladder configuration.");
            var expectedLevel = policy.RequirementLevelFor(owner.Level);
            foreach (var id in ids)
            {
                if (!requirementScopes.TryGetValue(id, out var requirement))
                    throw new DomainException($"{noun} {owner.BaseNumber} names a requirement revision that is not persisted.");
                if (requirement.ProjectId != owner.ProjectId || requirement.Level != expectedLevel)
                    throw new DomainException($"{noun} {owner.BaseNumber} has an exact parent with the wrong project or configured requirement level.");
                var inProcedureBaseline = revision.EffectiveBaselineId is Guid procedureBaseline
                    && baselineMemberships.Contains((procedureBaseline, id));
                var carriedIntoDescendantBaseline = false;
                if (!inProcedureBaseline
                    && revision.EffectiveBaselineId is Guid sourceBaseline
                    && requirement.EffectiveBaselineId != sourceBaseline
                    && baselineMemberships.Contains((requirement.EffectiveBaselineId, id)))
                {
                    var candidate = new ProcedureScope(revision.Id, revision.ProcedureId, revision.Revision,
                        revision.EffectiveBaselineId, owner.ProjectId, owner.Level, owner.ArtifactKind,
                        owner.BaseNumber, revision.State, revision.AuthorId, revision.ParentKind);
                    carriedIntoDescendantBaseline = await IsLatestActiveProcedureRevisionAsync(candidate, ct)
                        && await IsBaselineDescendantAsync(requirement.EffectiveBaselineId, sourceBaseline,
                            owner.ProjectId, ct);
                }
                if (!inProcedureBaseline && !carriedIntoDescendantBaseline
                    || revision.EffectiveBaselineId is not Guid governedBaseline
                    || !baselineScopes.TryGetValue(governedBaseline, out var procedureScope)
                    || procedureScope.ProjectId != owner.ProjectId)
                    throw new DomainException($"{noun} {owner.BaseNumber} names an exact parent outside its applicable governed baseline scope.");
            }
        }

        static bool IsConfirmedSuspectLifecycleEntry(
            Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TestRequirementCoverage> entry) =>
            entry.State == EntityState.Added
            && !entry.Entity.IsSuspect
            && entry.Entity.ConfirmedAt is not null;
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>>> FinalParentIdsAsync(
        IReadOnlyCollection<Guid> procedureRevisionIds, CancellationToken ct)
    {
        var ids = procedureRevisionIds.Distinct().ToHashSet();
        if (ids.Count == 0) return new Dictionary<Guid, IReadOnlyCollection<Guid>>();

        var parentIdsByProcedure = (await _db.TestCaseProcedureLinks.AsNoTracking()
                .Where(x => ids.Contains(x.ProcedureRevisionId))
                // Lifecycle-attached links are carried revalidation evidence, not a silent rewrite of the
                // Procedure revision's immutable authored parent selection. Closed links are admitted by
                // baseline/document read projections that deliberately consume the lifecycle outcome.
                .Where(x => x.ExactLinkSuspectLifecycleId == null)
                .Select(x => new { x.ProcedureRevisionId, x.CaseRevisionId })
                .ToListAsync(ct))
            .GroupBy(x => x.ProcedureRevisionId)
            .ToDictionary(x => x.Key, x => (IReadOnlyCollection<Guid>)x
                .Select(link => link.CaseRevisionId).ToHashSet());
        foreach (var id in ids)
            parentIdsByProcedure.TryAdd(id, new HashSet<Guid>());

        foreach (var entry in _db.ChangeTracker.Entries<TestCaseProcedureLink>()
                     .Where(x => ids.Contains(x.Entity.ProcedureRevisionId)))
        {
            var parentIds = (HashSet<Guid>)parentIdsByProcedure[entry.Entity.ProcedureRevisionId];
            var currentLifecycleIsAuthored = entry.Entity.ExactLinkSuspectLifecycleId is null;
            var originalLifecycleIsAuthored = entry.State == EntityState.Added
                || entry.Property(x => x.ExactLinkSuspectLifecycleId).OriginalValue is null;
            if (entry.State is EntityState.Deleted or EntityState.Modified && originalLifecycleIsAuthored)
                parentIds.Remove(entry.Entity.CaseRevisionId);
            if (entry.State != EntityState.Deleted && currentLifecycleIsAuthored)
                parentIds.Add(entry.Entity.CaseRevisionId);
        }
        return parentIdsByProcedure;
    }

    /// <summary>
    /// Exact parent links are many-to-many but never free-form: both revisions must be software artifacts in
    /// the same project/discipline, and when provenance is available they must name the same exact baseline.
    /// </summary>
    private async Task ValidateCaseProcedureLinksAsync(CancellationToken ct)
    {
        var changedLinks = _db.ChangeTracker.Entries<TestCaseProcedureLink>()
            .Where(x => x.State != EntityState.Unchanged).ToList();
        var procedureRevisionIds = changedLinks
            .Select(x => x.Entity.ProcedureRevisionId).Distinct().ToList();
        if (procedureRevisionIds.Count == 0) return;
        var addedPairs = changedLinks.Where(x => x.State == EntityState.Added)
            .Select(x => (x.Entity.CaseRevisionId, x.Entity.ProcedureRevisionId)).ToHashSet();
        var parentIdsByProcedure = await FinalParentIdsAsync(procedureRevisionIds, ct);
        var ids = procedureRevisionIds.Concat(parentIdsByProcedure.Values.SelectMany(x => x)).Distinct().ToList();
        var rows = await (from revision in _db.TestProcedureRevisions.AsNoTracking()
                          join procedure in _db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                          where ids.Contains(revision.Id)
                          select new ProcedureScope(revision.Id, revision.ProcedureId, revision.Revision,
                              revision.EffectiveBaselineId, procedure.ProjectId, procedure.Level,
                              procedure.ArtifactKind, procedure.BaseNumber, revision.State,
                              revision.AuthorId, revision.ParentKind))
            .ToDictionaryAsync(x => x.RevisionId, ct);
        foreach (var revision in _db.ChangeTracker.Entries<TestProcedureRevision>().Where(x => x.State != EntityState.Deleted
                     && ids.Contains(x.Entity.Id)))
        {
            var owner = _db.ChangeTracker.Entries<TestProcedure>().FirstOrDefault(x => x.Entity.Id == revision.Entity.ProcedureId)?.Entity
                ?? await _db.TestProcedures.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revision.Entity.ProcedureId, ct);
            if (owner is not null)
                rows[revision.Entity.Id] = new ProcedureScope(revision.Entity.Id, revision.Entity.ProcedureId,
                    revision.Entity.Revision, revision.Entity.EffectiveBaselineId, owner.ProjectId, owner.Level,
                    owner.ArtifactKind, owner.BaseNumber, revision.Entity.State,
                    revision.Entity.AuthorId, revision.Entity.ParentKind);
        }
        foreach (var procedureRevisionId in procedureRevisionIds)
        {
            if (!rows.TryGetValue(procedureRevisionId, out var procedure))
                throw new DomainException("An exact Case-to-Procedure link must name persisted revisions.");
            var migrationOwned = procedure.AuthorId == VerificationArtifactProfileSchema.GovernedMigrationActor
                && procedure.ParentKind == VerificationProcedureParentKind.Allocated;
            if (procedure.EffectiveBaselineId is Guid procedureBaseline
                && !await IsProcedureRevisionInBaselineAsync(procedureRevisionId, procedureBaseline, ct))
                throw new DomainException("A governed software Procedure revision must be selected in its exact baseline before it can name Case parents.");
            foreach (var caseRevisionId in parentIdsByProcedure[procedureRevisionId])
            {
                if (!rows.TryGetValue(caseRevisionId, out var @case))
                    throw new DomainException("An exact Case-to-Procedure link must name persisted revisions.");
                if (@case.ArtifactKind != VerificationArtifactKind.Case || @case.Level == TestProcedureLevel.System)
                    throw new DomainException("A Procedure parent must be a software Case revision, never a System Procedure or Requirement.");
                if (@case.State == TestProcedureState.Retired)
                    throw new DomainException("A Procedure parent must be a current active Case revision.");
                if (procedure.ArtifactKind != VerificationArtifactKind.Procedure || procedure.Level == TestProcedureLevel.System)
                throw new DomainException("An exact parent link must target a dormant software Procedure revision.");
                if (@case.ProjectId != procedure.ProjectId || @case.Level != procedure.Level)
                    throw new DomainException("Case and Procedure parent revisions must belong to the same project and software level.");
                if (procedure.EffectiveBaselineId is Guid governedProcedureBaseline
                    && !migrationOwned
                    && !await IsProcedureRevisionInBaselineAsync(caseRevisionId, governedProcedureBaseline, ct))
                    throw new DomainException("Case and Procedure parent revisions must be members of the same exact governed baseline.");
                // A migration-generated Procedure revision is an exact mirror of its source Case revision:
                // its governed baseline is copied from the Case revision, so identity equality replaces the
                // membership check that the cutover's baseline rebind intentionally supersedes.
                if (migrationOwned
                    && procedure.EffectiveBaselineId is Guid migrationProcedureBaseline
                    && @case.EffectiveBaselineId != migrationProcedureBaseline)
                    throw new DomainException(
                        "A migration-generated Procedure revision must share the exact governed baseline of its source Case revision.");
                if (migrationOwned
                    && procedure.EffectiveBaselineId is null
                    && @case.EffectiveBaselineId is not null)
                    throw new DomainException(
                        "A migration-generated Procedure revision must copy the governed baseline of its source Case revision.");
                // A historical exact relation remains immutable evidence after a later Case revision is
                // approved. Revalidate currency only for the newly authored or lifecycle-carried relation;
                // otherwise adding the successor relation would retroactively invalidate released history.
                if (!migrationOwned
                    && procedure.EffectiveBaselineId is null
                    && addedPairs.Contains((caseRevisionId, procedureRevisionId))
                    && !await IsLatestActiveProcedureRevisionAsync(@case, ct))
                    throw new DomainException("A dormant Procedure without a governed baseline must name the latest active Case revision.");
            }
            var parentBaselines = parentIdsByProcedure[procedureRevisionId]
                .Where(rows.ContainsKey)
                .Select(id => rows[id].EffectiveBaselineId)
                .ToList();
            var knownBaselines = parentBaselines.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
            var knownParentBaselineCount = parentBaselines.Count(x => x is not null);
            if (knownBaselines.Count > 1 || (knownBaselines.Count > 0 && knownParentBaselineCount != parentBaselines.Count))
                throw new DomainException("Case parent revisions must share one exact baseline; mixed or unknown build provenance is not allowed.");
            if (procedure.EffectiveBaselineId is null
                && knownBaselines.Count > 1)
                throw new DomainException("Case parent revisions must share one exact baseline when no governed Procedure baseline is recorded.");
        }
    }

    private async Task<bool> IsProcedureRevisionInBaselineAsync(Guid revisionId, Guid baselineId,
        CancellationToken ct)
    {
        var member = await _db.BaselineTestProcedures.AsNoTracking()
            .AnyAsync(x => x.BaselineId == baselineId && x.RevisionId == revisionId, ct);
        foreach (var entry in _db.ChangeTracker.Entries<BaselineTestProcedureSelection>()
                     .Where(x => x.Entity.BaselineId == baselineId && x.Entity.RevisionId == revisionId))
        {
            if (entry.State == EntityState.Deleted) member = false;
            else member = true;
        }
        return member;
    }

    private async Task<bool> IsLatestActiveProcedureRevisionAsync(ProcedureScope candidate,
        CancellationToken ct)
    {
        if (candidate.State == TestProcedureState.Retired) return false;
        var newer = await _db.TestProcedureRevisions.AsNoTracking().AnyAsync(x =>
            x.ProcedureId == candidate.ArtifactId
            && x.Revision > candidate.Revision, ct);
        if (newer) return false;
        return !_db.ChangeTracker.Entries<TestProcedureRevision>().Any(x =>
            x.State != EntityState.Deleted
            && x.Entity.ProcedureId == candidate.ArtifactId
            && x.Entity.Revision > candidate.Revision);
    }

    private async Task<bool> IsBaselineDescendantAsync(Guid descendantId, Guid ancestorId, Guid projectId,
        CancellationToken ct)
    {
        if (descendantId == ancestorId) return true;
        var visited = new HashSet<Guid>();
        var cursor = await _db.CandidateBaselines.AsNoTracking()
            .Where(x => x.Id == descendantId)
            .Select(x => new { x.Id, x.ProjectId, x.PredecessorBaselineId })
            .SingleOrDefaultAsync(ct);
        while (cursor is not null && visited.Add(cursor.Id))
        {
            if (cursor.ProjectId != projectId) return false;
            if (cursor.PredecessorBaselineId == ancestorId) return true;
            if (cursor.PredecessorBaselineId is not Guid predecessorId) return false;
            cursor = await _db.CandidateBaselines.AsNoTracking()
                .Where(x => x.Id == predecessorId)
                .Select(x => new { x.Id, x.ProjectId, x.PredecessorBaselineId })
                .SingleOrDefaultAsync(ct);
        }
        return false;
    }

}
