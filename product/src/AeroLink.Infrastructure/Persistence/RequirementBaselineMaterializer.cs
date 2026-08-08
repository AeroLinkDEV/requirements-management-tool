using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record MaterializationResult(string RequirementsHash, int ActiveRequirementCount, int CreatedRevisionCount);

public sealed class RequirementBaselineMaterializer(AeroLinkDbContext db, VerificationImpactService verificationImpact)
{
    public async Task<MaterializationResult> MaterializeAsync(Guid baselineId, string actorId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var baseline = await db.CandidateBaselines.Include(x => x.Selections).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == baselineId, ct)
            ?? throw new DomainException("Baseline not found.");
        if (baseline.State != CandidateBaselineState.Frozen) throw new DomainException("Freeze the baseline before materializing its requirements.");
        if (baseline.RequirementsMaterializedAt is not null) throw new DomainException("The requirement baseline is already materialized and immutable.");

        var artifacts = await db.Requirements.Where(x => x.ProjectId == baseline.ProjectId).ToListAsync(ct);
        var schemas = await db.ArtifactSchemas.AsNoTracking().Where(x=>x.ProjectId==baseline.ProjectId&&x.IsActive).ToDictionaryAsync(x=>x.AppliesTo,ct);
        var artifactByBase = artifacts.ToDictionary(x => x.BaseNumber, StringComparer.OrdinalIgnoreCase);
        var revisions = await db.RequirementRevisions.Where(x => artifacts.Select(a => a.Id).Contains(x.ArtifactId)).ToListAsync(ct);
        var current = new Dictionary<Guid, RequirementRevision>();
        if (baseline.PredecessorBaselineId is not null)
        {
            var predecessor = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == baseline.PredecessorBaselineId, ct)
                ?? throw new DomainException("The predecessor baseline does not exist.");
            if (predecessor.ProjectId != baseline.ProjectId || predecessor.RequirementsMaterializedAt is null)
                throw new DomainException("The predecessor must be a materialized baseline from the same project.");
            var predecessorItems = await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == predecessor.Id).ToListAsync(ct);
            var predecessorRevisionIds = predecessorItems.Select(x => x.RevisionId).ToList();
            var predecessorRevisions = await db.RequirementRevisions.AsNoTracking().Where(x => predecessorRevisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            foreach (var item in predecessorItems) current[item.ArtifactId] = predecessorRevisions[item.RevisionId];
        }

        var scrIds = baseline.Selections.Select(x => x.ChangeRequestId).ToList();
        var scrs = await db.SystemChangeRequests.AsNoTracking().Where(x => scrIds.Contains(x.Id)).Include(x => x.RequirementChanges).ToListAsync(ct);
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
            if (change.Kind == RequirementChangeKind.Introduce)
            {
                if (artifactByBase.ContainsKey(change.BaseNumber)) throw new DomainException($"{change.DisplayNumber} cannot be introduced because its stable identity already exists.");
                var artifact = new RequirementArtifact(baseline.ProjectId, change.BaseNumber, change.Level, now);
                db.Requirements.Add(artifact); artifactByBase.Add(artifact.BaseNumber, artifact);
                var revision = CreateRevision(artifact, change, pair.scr.Id, baseline.Id, now, RequirementRevisionState.Active);
                db.RequirementRevisions.Add(revision); revisions.Add(revision); current[artifact.Id] = revision; created++;
                AddProfile(revision,change,schemas,actorId,now);
                materialized.Add(new(pair.scr.Id, change.Id, change.Kind, null, revision.Id, change.DisplayNumber));
                continue;
            }

            if (!artifactByBase.TryGetValue(change.BaseNumber, out var existing) || !current.TryGetValue(existing.Id, out var prior))
                throw new DomainException($"{change.Kind} requires {change.BaseNumber} to be active in the predecessor or current baseline.");
            if (change.Revision <= prior.Revision) throw new DomainException($"{change.DisplayNumber} must have a revision greater than {prior.Revision:D2}.");
            var state = change.Kind == RequirementChangeKind.Retire ? RequirementRevisionState.Retired : RequirementRevisionState.Active;
            var next = CreateRevision(existing, change, pair.scr.Id, baseline.Id, now, state);
            db.RequirementRevisions.Add(next); revisions.Add(next); created++;
            AddProfile(next,change,schemas,actorId,now);
            materialized.Add(new(pair.scr.Id, change.Id, change.Kind, prior.Id, next.Id, change.DisplayNumber));
            if (state == RequirementRevisionState.Retired) current.Remove(existing.Id); else current[existing.Id] = next;
        }

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
        foreach (var allocation in proposed)
        {
            if (!revisionByChange.TryGetValue(allocation.Change.Id, out var source)) continue;
            var key = (source, allocation.Parent, RequirementTraceType.AllocatedFrom);
            if (!existingTraceKeys.Add(key)) continue;
            db.RequirementTraces.Add(new RequirementTraceLink(baseline.ProjectId, source, allocation.Parent,
                RequirementTraceType.AllocatedFrom,
                $"Prospective upward allocation approved in {allocation.Scr.DisplayNumber}: {allocation.Change.Rationale}", now));
        }

        await PlaceInChosenSectionsAsync(baseline.ProjectId, scrs, artifactByBase, actorId, now, ct);

        var artifactById = artifactByBase.Values.ToDictionary(x => x.Id);
        foreach (var item in current.OrderBy(x => artifactById[x.Key].BaseNumber))
            db.BaselineRequirements.Add(new BaselineRequirementSelection(baseline.Id, item.Key, item.Value.Id));
        var manifest = string.Join(";", current.OrderBy(x => artifactById[x.Key].BaseNumber)
            .Select(x => $"{artifactById[x.Key].BaseNumber}.{x.Value.Revision:D2}:{x.Value.Id}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        baseline.MarkRequirementsMaterialized(actorId, hash, current.Count, now);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
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
        IReadOnlyDictionary<string, RequirementArtifact> artifactByBase, string actorId, DateTimeOffset now,
        CancellationToken ct)
    {
        var chosen = scrs.SelectMany(scr => scr.RequirementChanges)
            .Where(change => change.TargetSectionId is not null && change.Kind != RequirementChangeKind.Retire)
            .ToList();
        if (chosen.Count == 0) return;

        var sectionIds = chosen.Select(x => x.TargetSectionId!.Value).Distinct().ToList();
        var sections = await (from node in db.SpecificationNodes.AsNoTracking()
                              join spec in db.RequirementSpecifications.AsNoTracking() on node.SpecificationId equals spec.Id
                              where sectionIds.Contains(node.Id) && spec.ProjectId == projectId
                                 && node.Type == SpecificationNodeType.Section
                              select new { node.Id, node.SpecificationId }).ToListAsync(ct);

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

    private static RequirementRevision CreateRevision(RequirementArtifact artifact, RequirementChange change, Guid changeRequestId,
        Guid baselineId, DateTimeOffset now, RequirementRevisionState state) =>
        new(artifact.Id, change.Revision, change.Statement, change.Rationale, change.VerificationMethod, state, changeRequestId, baselineId, now);

    private static IReadOnlyList<Guid> ProposedParents(RequirementChange change)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(change.ProposedUpstreamRevisionIdsJson) ?? []; }
        catch (JsonException) { return []; }
    }

    private void AddProfile(RequirementRevision revision,RequirementChange change,IReadOnlyDictionary<string,ArtifactSchemaDefinition> schemas,string actor,DateTimeOffset now)
    {
        if(schemas.TryGetValue(change.Level.ToString(),out var schema))db.RequirementRevisionProfiles.Add(new(revision.Id,schema.Id,string.IsNullOrWhiteSpace(change.RichText)?change.Statement:change.RichText,change.AttributesJson,"[]",actor,now));
    }
}
