using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record MaterializationResult(string RequirementsHash, int ActiveRequirementCount, int CreatedRevisionCount);

public sealed class RequirementBaselineMaterializer(AeroLinkDbContext db)
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

        var scrIds = baseline.Selections.Select(x => x.ScrId).ToList();
        var scrs = await db.SystemChangeRequests.AsNoTracking().Where(x => scrIds.Contains(x.Id)).Include(x => x.RequirementChanges).ToListAsync(ct);
        var created = 0;
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
                continue;
            }

            if (!artifactByBase.TryGetValue(change.BaseNumber, out var existing) || !current.TryGetValue(existing.Id, out var prior))
                throw new DomainException($"{change.Kind} requires {change.BaseNumber} to be active in the predecessor or current baseline.");
            if (change.Revision <= prior.Revision) throw new DomainException($"{change.DisplayNumber} must have a revision greater than {prior.Revision:D2}.");
            var state = change.Kind == RequirementChangeKind.Retire ? RequirementRevisionState.Retired : RequirementRevisionState.Active;
            var next = CreateRevision(existing, change, pair.scr.Id, baseline.Id, now, state);
            db.RequirementRevisions.Add(next); revisions.Add(next); created++;
            AddProfile(next,change,schemas,actorId,now);
            if (state == RequirementRevisionState.Retired) current.Remove(existing.Id); else current[existing.Id] = next;
        }

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

    private static RequirementRevision CreateRevision(RequirementArtifact artifact, RequirementChange change, Guid scrId,
        Guid baselineId, DateTimeOffset now, RequirementRevisionState state) =>
        new(artifact.Id, change.Revision, change.Statement, change.Rationale, change.VerificationMethod, state, scrId, baselineId, now);

    private void AddProfile(RequirementRevision revision,RequirementChange change,IReadOnlyDictionary<string,ArtifactSchemaDefinition> schemas,string actor,DateTimeOffset now)
    {
        if(schemas.TryGetValue(change.Level.ToString(),out var schema))db.RequirementRevisionProfiles.Add(new(revision.Id,schema.Id,string.IsNullOrWhiteSpace(change.RichText)?change.Statement:change.RichText,change.AttributesJson,"[]",actor,now));
    }
}
