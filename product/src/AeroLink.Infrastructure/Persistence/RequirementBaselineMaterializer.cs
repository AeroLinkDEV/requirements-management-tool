using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Imports;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record MaterializationResult(string RequirementsHash, int ActiveRequirementCount, int CreatedRevisionCount);

public sealed class RequirementBaselineMaterializer(AeroLinkDbContext db, VerificationImpactService verificationImpact,
    ILadderPolicy? policy = null, IProjectLadderPolicyResolver? policyResolver = null)
{
    public Task<MaterializationResult> MaterializeAsync(Guid baselineId, string actorId, DateTimeOffset now, CancellationToken ct)
        => MaterializeCoreAsync(baselineId, actorId, now, ct, allowLegacyHistoricalSeed: false);

    // The clean showcase creates and materializes a characterized pre-#738 release in one controlled seed
    // operation. This internal seam is deliberately unavailable through the normal DI/API materializer, so
    // migrated v1 rows selected into a new baseline cannot reuse the historical Unspecified exemption.
    internal Task<MaterializationResult> MaterializeLegacyHistoricalSeedAsync(Guid baselineId, string actorId,
        DateTimeOffset now, CancellationToken ct)
        => MaterializeCoreAsync(baselineId, actorId, now, ct, allowLegacyHistoricalSeed: true);

    private async Task<MaterializationResult> MaterializeCoreAsync(Guid baselineId, string actorId, DateTimeOffset now,
        CancellationToken ct, bool allowLegacyHistoricalSeed)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var baseline = await db.CandidateBaselines.Include(x => x.Selections).Include(x => x.ExternalPackageSelections).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == baselineId, ct)
            ?? throw new DomainException("Baseline not found.");
        if (baseline.State != CandidateBaselineState.Frozen) throw new DomainException("Freeze the baseline before materializing its requirements.");
        if (baseline.RequirementsMaterializedAt is not null) throw new DomainException("The requirement baseline is already materialized and immutable.");
        var ladderPolicy = policyResolver is null ? (policy ?? LegacyLadderPolicy.Instance)
            : await policyResolver.ResolveAsync(baseline.ProjectId, ct);
        using var savePolicyScope = db.UseSaveBoundaryPolicy(ladderPolicy);
        using var legacyHistoricalSeedScope = allowLegacyHistoricalSeed
            ? db.UseLegacyHistoricalSeed()
            : null;
        // Materialization is a current mutation seam, so ensure the active schema/specification projection
        // exists for this effective policy before creating profiles or placements. Historical inactive rows are
        // retained by the synchronizer but are never selected below.
        await new EnterpriseRequirementsService(db, ladderPolicy, policyResolver)
            .SynchronizeProjectAsync(baseline.ProjectId, actorId, ct);

        var artifacts = await db.Requirements.Where(x => x.ProjectId == baseline.ProjectId).ToListAsync(ct);
        var configuredDefinitions = ladderPolicy.Definitions
            .Where(x => x.Has(LevelCapabilities.HasRequirementsDocument) && x.RequirementsCatalogue is not null)
            .ToArray();
        var configuredSchemaKeys = configuredDefinitions.Select(x => x.RequirementsCatalogue!.SchemaKey)
            .ToHashSet(StringComparer.Ordinal);
        var schemas = await db.ArtifactSchemas.AsNoTracking()
            .Where(x => x.ProjectId == baseline.ProjectId && x.IsActive && configuredSchemaKeys.Contains(x.Key))
            .ToDictionaryAsync(x => x.AppliesTo, ct);
        var artifactByBase = artifacts.ToDictionary(x => x.BaseNumber, StringComparer.OrdinalIgnoreCase);
        var revisions = await db.RequirementRevisions.Where(x => artifacts.Select(a => a.Id).Contains(x.ArtifactId)).ToListAsync(ct);
        var current = new Dictionary<Guid, RequirementRevision>();
        var predecessorCurrent = new Dictionary<Guid, RequirementRevision>();
        Guid? predecessorBaselineId = baseline.PredecessorBaselineId;
        if (baseline.PredecessorBaselineId is not null)
        {
            var predecessor = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == baseline.PredecessorBaselineId, ct)
                ?? throw new DomainException("The predecessor baseline does not exist.");
            if (predecessor.ProjectId != baseline.ProjectId || predecessor.RequirementsMaterializedAt is null)
                throw new DomainException("The predecessor must be a materialized baseline from the same project.");
            var predecessorItems = await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == predecessor.Id).ToListAsync(ct);
            var predecessorRevisionIds = predecessorItems.Select(x => x.RevisionId).ToList();
            var predecessorRevisions = await db.RequirementRevisions.AsNoTracking().Where(x => predecessorRevisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            foreach (var item in predecessorItems)
            {
                var revision = predecessorRevisions[item.RevisionId];
                current[item.ArtifactId] = revision;
                predecessorCurrent[item.ArtifactId] = revision;
            }
        }

        var scrIds = baseline.Selections.Select(x => x.ChangeRequestId).ToList();
        var scrs = await db.SystemChangeRequests.AsNoTracking().Where(x => scrIds.Contains(x.Id)).Include(x => x.RequirementChanges).ToListAsync(ct);
        var packageIds = baseline.ExternalPackageSelections.Select(x => x.BaselineImportId).ToList();
        var packages = await db.BaselineImports.AsNoTracking().Where(x => packageIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        if (packages.Count != packageIds.Distinct().Count())
            throw new DomainException("A selected external package no longer exists.");
        foreach (var package in packages.Values)
        {
            if (package.State != BaselineImportState.Accepted)
                throw new DomainException("Only accepted external packages can be materialized.");
            if (package.ProjectId != baseline.ProjectId || (package.ReleaseId is not null && package.ReleaseId != baseline.ReleaseId))
                throw new DomainException("A selected external package does not belong to this project and release.");
            if (package.BoundCandidateBaselineId != baseline.Id || string.IsNullOrWhiteSpace(package.PackageManifestHash))
                throw new DomainException("A selected external package is not bound to this baseline.");
        }
        var packageItems = await db.BaselineImportPackageItems.AsNoTracking()
            .Where(x => packageIds.Contains(x.BaselineImportId)).OrderBy(x => x.BaselineImportId).ThenBy(x => x.BaseNumber)
            .ThenBy(x => x.Revision).ToListAsync(ct);
        foreach (var selection in baseline.ExternalPackageSelections)
        {
            var selectedItems = packageItems.Where(x => x.BaselineImportId == selection.BaselineImportId).ToList();
            if (selectedItems.Count == 0 || !string.Equals(BaselineImportPackageManifest.Hash(selectedItems), selection.PackageContentHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new DomainException("The staged external package contents changed after selection.");
        }
        if (packageItems.GroupBy(x => (x.BaselineImportId, x.SourceIdentityId)).Any(x => x.Count() > 1))
            throw new DomainException("An external package contains duplicate source identities.");
        var sourceIdentityIds = packageItems.Select(x => x.SourceIdentityId).Distinct().ToList();
        var sourceIdentities = await db.SourceIdentities.AsNoTracking().Where(x => sourceIdentityIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        if (sourceIdentities.Count != sourceIdentityIds.Count)
            throw new DomainException("An external package item references a missing source identity.");
        var sourceMemberships = await db.BaselineImportSourceIdentityMemberships.AsNoTracking()
            .Where(x => packageIds.Contains(x.BaselineImportId) && sourceIdentityIds.Contains(x.SourceIdentityId))
            .ToListAsync(ct);
        foreach (var item in packageItems)
        {
            if (!packages.TryGetValue(item.BaselineImportId, out var package)
                || item.ProjectId != baseline.ProjectId || package.ProjectId != baseline.ProjectId)
                throw new DomainException("An external package item does not belong to this project.");
            var identity = sourceIdentities[item.SourceIdentityId];
            var membership = sourceMemberships.SingleOrDefault(x => x.BaselineImportId == item.BaselineImportId
                && x.SourceIdentityId == item.SourceIdentityId);
            if (identity.ProjectId != baseline.ProjectId || membership is null || !membership.InImportedBaseline)
                throw new DomainException("An external package item has an inconsistent source identity.");
            if (!string.Equals(identity.SourceIdentifier, item.SourceIdentifier, StringComparison.Ordinal))
                throw new DomainException("An external package item does not preserve its source identifier.");
        }
        // DEC-071 superseded DEC-062: review submission, baseline selection/freeze/materialization and
        // integrity checkpoints do not require the former five impact dispositions, and existing stored
        // disposition data remains historical and is not rewritten. A legacy change whose
        // ImpactDispositionJson predates the capability (often "{}", written by the introducing migration's
        // default) must therefore not block materialization.
        var created = 0;
        // What each change became, so verification work can bind to exact revisions once they exist.
        var materialized = new List<MaterializedRequirementChange>();
        foreach (var pair in scrs.SelectMany(scr => scr.RequirementChanges.Select(change => new { scr, change }))
                     .OrderBy(x => x.scr.DisplayNumber).ThenBy(x => x.change.BaseNumber).ThenBy(x => x.change.Revision))
        {
            var change = pair.change;
            LevelDefinition definition;
            try { definition = ladderPolicy.Definition(change.Level); }
            catch (DomainException) { throw new DomainException($"The configured project ladder does not contain {change.Level}."); }
            // ICD is a controlled, traceable requirement level without a generated requirements document.
            // Its immutable RequirementRevision still belongs in the baseline; only the structured profile
            // and specification placement are omitted.
            var hasRequirementsDocument = definition.Has(LevelCapabilities.HasRequirementsDocument)
                && definition.RequirementsCatalogue is not null;
            if (change.Kind == RequirementChangeKind.Introduce)
            {
                if (artifactByBase.ContainsKey(change.BaseNumber)) throw new DomainException($"{change.DisplayNumber} cannot be introduced because its stable identity already exists.");
                var artifact = new RequirementArtifact(baseline.ProjectId, change.BaseNumber, change.Level, now);
                db.Requirements.Add(artifact); artifactByBase.Add(artifact.BaseNumber, artifact);
                var introSelection = ResolveParentSelection(ladderPolicy, change, requireComplete: true,
                    allowLegacyUnspecified: allowLegacyHistoricalSeed
                        && pair.scr.SnapshotContractVersion < SystemChangeRequest.CurrentSnapshotContractVersion);
                var revision = CreateRevision(artifact, change, pair.scr.Id, baseline.Id, now,
                    RequirementRevisionState.Active, introSelection);
                db.RequirementRevisions.Add(revision); revisions.Add(revision); current[artifact.Id] = revision; created++;
                if (hasRequirementsDocument) AddProfile(revision,change,schemas,actorId,now);
                materialized.Add(new(pair.scr.Id, change.Id, change.Kind, null, revision.Id, change.DisplayNumber));
                continue;
            }

            if (!artifactByBase.TryGetValue(change.BaseNumber, out var existing) || !current.TryGetValue(existing.Id, out var prior))
                throw new DomainException($"{change.Kind} requires {change.BaseNumber} to be active in the predecessor or current baseline.");
            if (change.Revision <= prior.Revision) throw new DomainException($"{change.DisplayNumber} must have a revision greater than {prior.Revision:D2}.");
            var state = change.Kind == RequirementChangeKind.Retire ? RequirementRevisionState.Retired : RequirementRevisionState.Active;
            var nextSelection = state == RequirementRevisionState.Retired
                ? new ParentSelectionData(RequirementParentKind.Unspecified, [], "")
                : ResolveParentSelection(ladderPolicy, change, requireComplete: false,
                    allowLegacyUnspecified: allowLegacyHistoricalSeed
                        && pair.scr.SnapshotContractVersion < SystemChangeRequest.CurrentSnapshotContractVersion);
            var next = CreateRevision(existing, change, pair.scr.Id, baseline.Id, now, state, nextSelection);
            db.RequirementRevisions.Add(next); revisions.Add(next); created++;
            if (hasRequirementsDocument) AddProfile(next,change,schemas,actorId,now);
            materialized.Add(new(pair.scr.Id, change.Id, change.Kind, prior.Id, next.Id, change.DisplayNumber));
            if (state == RequirementRevisionState.Retired) current.Remove(existing.Id); else current[existing.Id] = next;
        }

        // External package items are already reconciled source content, not change requests. They become
        // effective only here, alongside the ordinary predecessor/SCR pass, and retain the package that
        // committed this revision even when the SourceIdentity was first seen in an earlier import.
        foreach (var item in packageItems.OrderBy(x => x.BaseNumber).ThenBy(x => x.Revision).ThenBy(x => x.Id))
        {
            var customer = ladderPolicy.Definition(RequirementLevel.Customer);
            if (!customer.UsesExternalOrigin || customer.Has(LevelCapabilities.HasChangeControl)
                || customer.Has(LevelCapabilities.HasVerification) || customer.Has(LevelCapabilities.HasRequirementsDocument))
                throw new DomainException("The Customer ladder definition must be external-origin only, without AeroLink change control, verification, or a requirements document.");
            if (!artifactByBase.TryGetValue(item.BaseNumber, out var artifact))
            {
                if (item.Revision != 0)
                    throw new DomainException($"{item.BaseNumber} must start at revision 00.");
                artifact = new RequirementArtifact(baseline.ProjectId, item.BaseNumber, RequirementLevel.Customer, now);
                db.Requirements.Add(artifact); artifactByBase.Add(artifact.BaseNumber, artifact);
                var revision = RequirementRevision.FromExternalSourcePackage(artifact.Id, item.Revision, item.Statement,
                    item.Rationale, RequirementRevisionState.Active, item.BaselineImportId, baseline.Id, now);
                db.RequirementRevisions.Add(revision); revisions.Add(revision); current[artifact.Id] = revision; created++;
                db.SourceIdentityLinks.Add(sourceIdentities[item.SourceIdentityId].LinkToFromImport(revision.Id, item.BaselineImportId, now));
                continue;
            }

            if (artifact.Level != RequirementLevel.Customer || !current.TryGetValue(artifact.Id, out var prior))
                throw new DomainException($"{item.BaseNumber} is not active in the predecessor baseline.");
            if (item.Revision <= prior.Revision)
                throw new DomainException($"{item.BaseNumber}.{item.Revision:D2} must have a revision greater than {prior.Revision:D2}.");
            var next = RequirementRevision.FromExternalSourcePackage(artifact.Id, item.Revision, item.Statement,
                item.Rationale, RequirementRevisionState.Active, item.BaselineImportId, baseline.Id, now);
            db.RequirementRevisions.Add(next); revisions.Add(next); current[artifact.Id] = next; created++;
            db.SourceIdentityLinks.Add(sourceIdentities[item.SourceIdentityId].LinkToFromImport(next.Id, item.BaselineImportId, now));
        }

        // Make the target baseline's exact requirement membership visible to the save-boundary integrity
        // check before verification coverage is carried forward. The selection is still in this transaction;
        // no released history is being rewritten.
        var artifactById = artifactByBase.Values.ToDictionary(x => x.Id);
        foreach (var item in current.OrderBy(x => artifactById[x.Key].BaseNumber))
            db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, item.Key, item.Value.Id));

        // Requirement revisions exist for the first time here, so this is the earliest point at which
        // verification work can bind to them, coverage can carry forward, and a stranded procedure is visible.
        await verificationImpact.ApplyMaterializationAsync(baseline.ProjectId, baseline.ReleaseId, materialized, actorId, now, ct);

        var revisionByChange = materialized.ToDictionary(x => x.RequirementChangeId, x => x.RevisionId);
        var proposed = scrs.SelectMany(x => x.RequirementChanges.Select(change => new { Scr = x, Change = change }))
            .SelectMany(x => ProposedParents(x.Change).Select(parent => new { x.Scr, x.Change, Parent = parent }))
            .ToList();
        var existingTraceKeys = (await db.RequirementTraces.AsNoTracking()
                .Where(x => x.ProjectId == baseline.ProjectId)
                .Select(x => new { x.SourceRevisionId, x.TargetRevisionId, x.Type }).ToListAsync(ct))
                .Select(x => (x.SourceRevisionId, x.TargetRevisionId, x.Type)).ToHashSet();
        var baselineParentIds = (await db.BaselineRequirements.AsNoTracking()
            .Where(x => x.BaselineId == baseline.Id)
            .Select(x => x.RevisionId).ToListAsync(ct)).ToHashSet();
        baselineParentIds.UnionWith(current.Values.Select(x => x.Id));
        var parentLevels = await (from revision in db.RequirementRevisions.AsNoTracking()
                                  join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                  where proposed.Select(x => x.Parent).Contains(revision.Id)
                                      && artifact.ProjectId == baseline.ProjectId
                                  select new { revision.Id, artifact.Level }).ToDictionaryAsync(x => x.Id, x => x.Level, ct);
        foreach (var allocation in proposed)
        {
            if (!revisionByChange.TryGetValue(allocation.Change.Id, out var source)) continue;
            if (!parentLevels.TryGetValue(allocation.Parent, out var parentLevel))
                throw new DomainException("An upstream allocation must reference an exact requirement revision.");
            if (!baselineParentIds.Contains(allocation.Parent))
                throw new DomainException("An upstream allocation must reference a current exact revision from this baseline.");
            var allowedParentLevels = ladderPolicy.ParentLevels(allocation.Change.Level);
            if (!allowedParentLevels.Contains(parentLevel))
                throw new DomainException(
                    $"{allocation.Change.DisplayNumber} names {parentLevel}, which is not a configured exact parent level for {allocation.Change.Level}.");
            var sourceChange = allocation.Change;
            if (RequirementAuthoringJson.IsDerived(sourceChange.AttributesJson))
                throw new DomainException("A derived requirement cannot carry exact upstream allocations.");
            RequirementTracePolicy.Validate(ladderPolicy, allocation.Change.Level, parentLevel, RequirementTraceType.AllocatedFrom);
            var key = (source, allocation.Parent, RequirementTraceType.AllocatedFrom);
            if (!existingTraceKeys.Add(key)) continue;
            db.RequirementTraces.Add(new RequirementTraceLink(baseline.ProjectId, source, allocation.Parent,
                RequirementTraceType.AllocatedFrom,
                $"Prospective upward allocation approved in {allocation.Scr.DisplayNumber}: {allocation.Change.Rationale}", now));
        }

        // Exact requirement traces belong to the baseline that contains both endpoints. Carrying them here,
        // before the baseline membership and revision rows are committed, keeps the link and its suspect
        // evidence in the same transaction. Reconciliation is intentionally reporting-only; it must never
        // manufacture relationship history after a candidate has already been frozen.
        if (predecessorBaselineId is not null && predecessorCurrent.Count > 0)
        {
            var predecessorRevisionIds = predecessorCurrent.Values.Select(x => x.Id).ToList();
            var predecessorTraces = await db.RequirementTraces.AsNoTracking()
                .Where(x => x.ProjectId == baseline.ProjectId
                    && predecessorRevisionIds.Contains(x.SourceRevisionId)
                    && predecessorRevisionIds.Contains(x.TargetRevisionId))
                .ToListAsync(ct);
            if (ladderPolicy is not ILegacyLadderCompatibilityPolicy)
            {
                var endpointIds = predecessorTraces.SelectMany(x => new[] { x.SourceRevisionId, x.TargetRevisionId }).Distinct().ToList();
                var endpointLevels = await (from revision in db.RequirementRevisions.AsNoTracking()
                                            join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                            where endpointIds.Contains(revision.Id)
                                            select new { revision.Id, artifact.Level }).ToDictionaryAsync(x => x.Id, x => x.Level, ct);
                predecessorTraces = predecessorTraces.Where(trace => endpointLevels.TryGetValue(trace.SourceRevisionId, out var source)
                    && endpointLevels.TryGetValue(trace.TargetRevisionId, out var target)
                    && IsConfiguredTrace(ladderPolicy, source, target, trace.Type)).ToList();
            }
            var predecessorArtifactByRevision = predecessorCurrent.Values.ToDictionary(x => x.Id, x => x.ArtifactId);
            var currentTraceKeys = existingTraceKeys;
            foreach (var predecessorTrace in predecessorTraces)
            {
                if (!predecessorArtifactByRevision.TryGetValue(predecessorTrace.SourceRevisionId, out var sourceArtifact)
                    || !predecessorArtifactByRevision.TryGetValue(predecessorTrace.TargetRevisionId, out var targetArtifact)) continue;
                var source = current.TryGetValue(sourceArtifact, out var sourceRevision) ? sourceRevision : null;
                var target = current.TryGetValue(targetArtifact, out var targetRevision) ? targetRevision : null;
                if (source is null || target is null) continue;
                var key = (source.Id, target.Id, predecessorTrace.Type);
                if (!currentTraceKeys.Add(key)) continue;

                var carried = new RequirementTraceLink(baseline.ProjectId, source.Id, target.Id, predecessorTrace.Type,
                    $"Carried forward from exact {predecessorTrace.SourceRevisionId} → {predecessorTrace.TargetRevisionId} in baseline {baseline.DisplayNumber}.", now);
                db.RequirementTraces.Add(carried);
                if (target.Id != predecessorTrace.TargetRevisionId)
                {
                    var causeKind = target.OriginKind == RequirementRevisionOriginKind.ExternalSourcePackage
                        ? ExactLinkLifecycleCauseKind.ExternalBaselineImport
                        : ExactLinkLifecycleCauseKind.InternalRequirementRevision;
                    var causeImport = target.SourceBaselineImportId;
                    Guid? causeRevision = causeKind == ExactLinkLifecycleCauseKind.InternalRequirementRevision ? target.Id : null;
                    if (causeKind == ExactLinkLifecycleCauseKind.ExternalBaselineImport
                        && (causeImport is null || !packageIds.Contains(causeImport.Value)))
                        throw new DomainException("An external suspect trace cause must be the selected package that created the exact target revision.");
                    var lifecycle = ExactLinkSuspectLifecycle.Raise(baseline.ProjectId, ExactLinkKind.RequirementTrace, carried.Id, causeKind,
                        causeRevision, causeImport, actorId,
                        $"The exact upstream target changed from {predecessorTrace.TargetRevisionId} to {target.Id}; direct downstream trace requires reassessment.", now);
                    carried.AttachExactLinkLifecycle(lifecycle.Id);
                    db.ExactLinkSuspectLifecycles.Add(lifecycle);
                    db.ExactLinkSuspectEvents.AddRange(lifecycle.Events);
                }
            }
        }

        await PlaceInChosenSectionsAsync(baseline.ProjectId, scrs, artifactByBase, ladderPolicy, actorId, now, ct);

        var manifest = string.Join(";", current.OrderBy(x => artifactById[x.Key].BaseNumber)
            .Select(x => $"{artifactById[x.Key].BaseNumber}.{x.Value.Revision:D2}:{x.Value.Id}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        baseline.MarkRequirementsMaterialized(actorId, hash, current.Count, now);
        var priorSealActor = db.LadderSealActor;
        db.LadderSealActor = actorId;
        try
        {
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        }
        finally { db.LadderSealActor = priorSealActor; }
        return new MaterializationResult(hash, current.Count, created);
    }

    /// <summary>
    /// Puts each requirement where its author said it belongs.
    ///
    /// Section membership is a `SpecificationNode` row, and until now nothing created one on the authoring path:
    /// an introduced requirement was placed by a backfill that assigns a section from a hash of its number, and a
    /// modification could not move one at all. So a change request could say what a requirement means and not
    /// where it goes, which is half of what an author is deciding.
    ///
    /// Applied here rather than at approval because this is where the requirement first exists to be placed. A
    /// change with no chosen section is left alone: for a modification that means staying put, and for an
    /// introduction it means the existing placement rule still decides — so this is additive and no proposal has
    /// to name a section to be valid.
    ///
    /// Ignores a section belonging to another project's specification. A stale identifier from a copied draft
    /// would otherwise place a requirement into a document it has nothing to do with.
    /// </summary>
    private async Task PlaceInChosenSectionsAsync(Guid projectId, IReadOnlyList<SystemChangeRequest> scrs,
        IReadOnlyDictionary<string, RequirementArtifact> artifactByBase, ILadderPolicy ladderPolicy,
        string actorId, DateTimeOffset now,
        CancellationToken ct)
    {
        var chosen = scrs.SelectMany(scr => scr.RequirementChanges)
            .Where(change => change.TargetSectionId is not null && change.Kind != RequirementChangeKind.Retire)
            .ToList();
        if (chosen.Count == 0) return;

        var sectionIds = chosen.Select(x => x.TargetSectionId!.Value).Distinct().ToList();
        var activeSpecifications = await db.RequirementSpecifications.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.IsActive).ToListAsync(ct);
        var effectiveSpecificationIds = activeSpecifications
            .Where(specification => ladderPolicy.Definitions.Any(definition =>
                definition.Has(LevelCapabilities.HasRequirementsDocument)
                && definition.RequirementsCatalogue is not null
                && definition.Level.ToString() == specification.Level
                && definition.RequirementsCatalogue.SpecificationNumber == specification.DocumentNumber))
            .Select(x => x.Id).ToHashSet();
        var sections = await (from node in db.SpecificationNodes.AsNoTracking()
                              join spec in db.RequirementSpecifications.AsNoTracking() on node.SpecificationId equals spec.Id
                              where sectionIds.Contains(node.Id) && effectiveSpecificationIds.Contains(spec.Id)
                                 && node.Type == SpecificationNodeType.Section
                              select new { node.Id, node.SpecificationId, Level = spec.Level }).ToListAsync(ct);

        var artifactIds = chosen.Select(x => artifactByBase.TryGetValue(x.BaseNumber, out var a) ? a.Id : Guid.Empty)
            .Where(x => x != Guid.Empty).ToList();
        // Tracked, not AsNoTracking: an existing placement is moved rather than duplicated, and a requirement in
        // two sections at once would appear twice in the generated document.
        var existing = await db.SpecificationNodes
            .Where(x => x.RequirementArtifactId != null && artifactIds.Contains(x.RequirementArtifactId.Value))
            .ToListAsync(ct);

        foreach (var change in chosen)
        {
            var section = sections.SingleOrDefault(x => x.Id == change.TargetSectionId!.Value);
            if (section is null)
                throw new DomainException($"{change.DisplayNumber} names a section that is no longer available in this project.");
            if (!string.Equals(section.Level, change.Level.ToString(), StringComparison.Ordinal))
                throw new DomainException($"{change.DisplayNumber} names a section from an inactive or different effective specification.");
            if (!artifactByBase.TryGetValue(change.BaseNumber, out var artifact)) continue;
            var placement = existing.SingleOrDefault(x => x.RequirementArtifactId == artifact.Id);
            if (placement is null)
                db.SpecificationNodes.Add(new SpecificationNode(section.SpecificationId, section.Id,
                    StablePosition(artifact.BaseNumber), SpecificationNodeType.Requirement, "", artifact.Id, actorId, now));
            else if (placement.SpecificationId == section.SpecificationId)
                // Only the parent section moves. A requirement's level fixes which specification it belongs to,
                // and a modification cannot change its level, so a chosen section in a different document would
                // be a stale identifier rather than an intention — left alone rather than acted on.
                placement.UpdateDraft(section.Id, placement.Position, placement.Heading, actorId, now);
        }
    }

    /// <summary>
    /// Where in the section a requirement sits, derived from its number so it is the same on every machine.
    ///
    /// The same rule the workspace backfill uses, for the same reason: an ordering that depends on when a row
    /// happened to be written would put the same requirements in a different order in two copies of one document.
    /// </summary>
    private static int StablePosition(string baseNumber)
    {
        var digits = new string(baseNumber.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : Math.Abs(baseNumber.GetHashCode() % 100000);
    }

    private sealed record ParentSelectionData(RequirementParentKind Kind, IReadOnlyList<Guid> ParentRevisionIds,
        string DerivedRationale);

    private static ParentSelectionData ResolveParentSelection(ILadderPolicy policy, RequirementChange change,
        bool requireComplete, bool allowLegacyUnspecified = false)
    {
        var parents = ProposedParents(change);
        _ = requireComplete;
        IReadOnlyList<RequirementLevel> allowed;
        try
        {
            _ = policy.Definition(change.Level);
            allowed = policy.ParentLevels(change.Level);
        }
        catch (DomainException ex)
        {
            throw new DomainException($"The configured project ladder cannot resolve {change.Level} parent topology: {ex.Message}");
        }
        var derived = RequirementAuthoringJson.IsDerived(change.AttributesJson);
        if (allowed.Count == 0)
        {
            if (parents.Count != 0)
                throw new DomainException($"{change.DisplayNumber} is a configured root and cannot carry upstream allocations.");
            return new(RequirementParentKind.Unspecified, [], "");
        }
        if (allowLegacyUnspecified && !derived && parents.Count == 0)
            return new(RequirementParentKind.Unspecified, [], "");
        var kind = derived ? RequirementParentKind.Derived : RequirementParentKind.Allocated;
        var rationale = derived ? change.Rationale.Trim() : "";
        ExactParentSelectionPolicy.Validate(
            kind == RequirementParentKind.Derived ? ExactParentClassification.Derived : ExactParentClassification.Allocated,
            parents, rationale, "requirement revision");
        return new(kind, ExactParentSelectionPolicy.NormalizeIds(parents, "requirement revision"), rationale);
    }

    private static RequirementRevision CreateRevision(RequirementArtifact artifact, RequirementChange change, Guid changeRequestId,
        Guid baselineId, DateTimeOffset now, RequirementRevisionState state, ParentSelectionData selection) =>
        new(artifact.Id, change.Revision, change.Statement, change.Rationale, change.VerificationMethod, state,
            changeRequestId, baselineId, now, selection.Kind, selection.DerivedRationale,
            selection.ParentRevisionIds);

    private static IReadOnlyList<Guid> ProposedParents(RequirementChange change)
    {
        try
        {
            return ExactParentSelectionPolicy.NormalizeIds(
                JsonSerializer.Deserialize<List<Guid>>(string.IsNullOrWhiteSpace(change.ProposedUpstreamRevisionIdsJson)
                    ? "[]" : change.ProposedUpstreamRevisionIdsJson) ?? [], change.DisplayNumber);
        }
        catch (JsonException)
        {
            throw new DomainException($"{change.DisplayNumber} carries malformed exact upstream revisions.");
        }
    }

    private static bool IsConfiguredTrace(ILadderPolicy policy, RequirementLevel source, RequirementLevel target,
        RequirementTraceType type)
    {
        try { RequirementTracePolicy.Validate(policy, source, target, type); return true; }
        catch (DomainException) { return false; }
    }

    private void AddProfile(RequirementRevision revision,RequirementChange change,IReadOnlyDictionary<string,ArtifactSchemaDefinition> schemas,string actor,DateTimeOffset now)
    {
        if (!schemas.TryGetValue(change.Level.ToString(), out var schema))
            throw new DomainException($"No active requirement schema is configured for {change.Level}.");
        db.RequirementRevisionProfiles.Add(new(revision.Id,schema.Id,string.IsNullOrWhiteSpace(change.RichText)?change.Statement:change.RichText,change.AttributesJson,"[]",actor,now));
    }
}
