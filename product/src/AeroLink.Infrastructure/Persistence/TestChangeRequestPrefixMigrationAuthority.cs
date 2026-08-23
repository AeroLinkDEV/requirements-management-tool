using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// Completes #723 after SQL has relabelled structured Test Change Request identities. SQL cannot regenerate
/// stored controlled bytes or truthfully derive a replacement review snapshot hash, so this authority owns the
/// renderer/domain hand-off. It is restart-safe, records real replacement hashes, and throws on any missing
/// source bytes or unreconstructible current signed package rather than claiming a partial upgrade.
/// </summary>
public sealed class TestChangeRequestPrefixMigrationAuthority(
    AeroLinkDbContext db, ControlledOutputGenerator generator, EvidenceFileStore files)
{
    public const string MigrationMarker = "VerificationIdentityMigration.TestChangeRequests.v1";
    private const string CompletedEvent = MigrationMarker + ".Completed";
    private const string Actor = "aerolink-migration";

    public async Task EnsureCompletedAsync(CancellationToken ct = default)
    {
        if (!db.Database.IsNpgsql()) return;
        if (await db.SecurityAuditEvents.AsNoTracking().AnyAsync(x => x.EventType == CompletedEvent
                && x.Target == "test-change-request-identities", ct)) return;

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var candidateDocuments = await db.ControlledDocuments
                .Where(x => x.Type == ControlledDocumentType.SystemTestProcedures
                    || x.Type == ControlledDocumentType.HighLevelTestCases
                    || x.Type == ControlledDocumentType.LowLevelTestCases)
                .OrderBy(x => x.Id).ToListAsync(ct);
            // A document is affected only when its exact generation-time procedure snapshot carries a
            // structured source TCR. This preserves legacy/unattributed documents and avoids regenerating
            // every document merely because its enum type is a software verification type.
            var documents = new List<ControlledDocument>();
            var affectedBaselineIds = new HashSet<Guid>();
            foreach (var candidate in candidateDocuments)
            {
                var level = candidate.Type switch
                {
                    ControlledDocumentType.SystemTestProcedures => TestProcedureLevel.System,
                    ControlledDocumentType.HighLevelTestCases => TestProcedureLevel.HighLevel,
                    ControlledDocumentType.LowLevelTestCases => TestProcedureLevel.LowLevel,
                    _ => throw new InvalidOperationException($"Unsupported migration document type {candidate.Type}.")
                };
                var snapshot = await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(
                    db, candidate.BaselineId, level, candidate.GeneratedAt, ct);
                if (!snapshot.Rows.Any(x => x.SourceTestChangeRequestId is not null)) continue;
                documents.Add(candidate);
                affectedBaselineIds.Add(candidate.BaselineId);
            }
            var artifacts = await db.ControlledDocumentArtifacts
                .Where(x => documents.Select(d => d.Id).Contains(x.DocumentId))
                .OrderBy(x => x.DocumentId).ThenBy(x => x.Format).ToListAsync(ct);
            var renditionByArtifactId = new Dictionary<Guid, (string OldHash, string NewHash)>();
            var renditionByDocumentId = new Dictionary<Guid, (string OldHash, string NewHash)>();

            await QueueAffectedDocumentSignatureSupersessionsAsync(documents, artifacts, now, ct);
            await RecomputeProcedureManifestsAsync(affectedBaselineIds, now, ct);
            await db.SaveChangesAsync(ct);
            foreach (var document in documents)
            {
                var manifestHash = await db.CandidateBaselines.AsNoTracking()
                    .Where(x => x.Id == document.BaselineId)
                    .Select(x => x.TestProceduresHash).SingleOrDefaultAsync(ct);
                if (string.IsNullOrWhiteSpace(manifestHash))
                    throw new InvalidOperationException($"TCR prefix migration cannot render document {document.Id} without a verification manifest hash.");
                var contentBasis = $"{manifestHash}|{document.Type}|{document.ArtifactCount}|{Actor}";
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contentBasis))).ToLowerInvariant();
                var oldDocumentHash = document.ContentHash;
                renditionByDocumentId[document.Id] = (oldDocumentHash, contentHash);
                document.RecordMigrationContentBasis(contentHash, now);
                db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                    "VerificationIdentityMigration.DocumentContentBasisRewritten", Actor,
                    $"ControlledDocument:{document.Id}", "Succeeded",
                    Json(new
                    {
                        migration = MigrationMarker,
                        documentId = document.Id,
                        oldContentHash = oldDocumentHash,
                        newContentHash = contentHash,
                        storedArtifactCount = artifacts.Count(x => x.DocumentId == document.Id),
                        outputBytesRegenerated = artifacts.Any(x => x.DocumentId == document.Id),
                        reason = artifacts.Any(x => x.DocumentId == document.Id)
                            ? "Affected document content basis was refreshed before governed stored-rendition regeneration."
                            : "Affected document has no stored rendition; only the exact on-demand content basis was refreshed."
                    }), "", now));
            }
            await db.SaveChangesAsync(ct);

            foreach (var artifact in artifacts)
            {
                ct.ThrowIfCancellationRequested();
                if (!files.Exists(artifact.StorageKey))
                    throw new InvalidOperationException($"TCR prefix migration cannot read stored rendition {artifact.Id} ({artifact.StorageKey}).");
                var oldHash = artifact.Sha256;
                var oldStorageKey = artifact.StorageKey;
                var output = await generator.GenerateAsync(artifact.DocumentId, artifact.Format, ct)
                    ?? throw new InvalidOperationException($"TCR prefix migration could not regenerate document {artifact.DocumentId} ({artifact.Format}).");
                await using var content = new MemoryStream(output.Content, writable: false);
                var stored = await files.StoreAsync(content, output.FileName, output.ContentType, ct);
                artifact.ReplaceMigrationRendition(stored.StorageKey, stored.OriginalFileName,
                    stored.ContentType, stored.Size, stored.Sha256, now);
                renditionByArtifactId[artifact.Id] = (oldHash, stored.Sha256);
                db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                    "VerificationIdentityMigration.DocumentRenditionRewritten", Actor,
                    $"ControlledDocumentArtifact:{artifact.Id}", "Succeeded",
                    Json(new
                    {
                        migration = MigrationMarker, documentId = artifact.DocumentId, format = artifact.Format,
                        oldStorageKey, oldContentHash = oldHash, newStorageKey = stored.StorageKey,
                        newContentHash = stored.Sha256,
                        reason = "Regenerated through ControlledOutputGenerator after structured TCR identity migration."
                    }), "", now));
            }
            var reviewCycleRenditions = await RecomputeReviewCycleSnapshotsAsync(now, ct);
            var supersededSignatures = await CompleteSignatureSupersessionsAsync(
                renditionByArtifactId, renditionByDocumentId, reviewCycleRenditions, now, ct);

            db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                CompletedEvent, Actor, "test-change-request-identities", "Succeeded",
                Json(new
                {
                    migration = MigrationMarker, renderedDocuments = documents.Count,
                    renderedArtifacts = artifacts.Count, reviewCyclesRewritten = reviewCycleRenditions.Count,
                    signaturesSuperseded = supersededSignatures, systemPrefix = "SYSTPCR",
                    highLevelPrefix = "HLRTCCR", lowLevelPrefix = "LLRTCCR", forwardOnly = true
                }), "", now));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Test Change Request prefix migration is incomplete; startup is fail-closed until stored bytes and signature evidence can be regenerated.", ex);
        }
    }

    private async Task<Dictionary<Guid, (Guid ReviewId, string OldHash, string NewHash)>> RecomputeReviewCycleSnapshotsAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        var reviews = await db.TestChangeReviews
            .Include(x => x.ProcedureChanges).Include(x => x.AdditionalSources).Include(x => x.ReviewCycles)
            .Where(x => x.Discipline == TestChangeReviewDiscipline.System
                || x.Discipline == TestChangeReviewDiscipline.HighLevelSoftware
                || x.Discipline == TestChangeReviewDiscipline.LowLevelSoftware)
            .ToListAsync(ct);
        var reviewIds = reviews.Select(x => x.Id).ToList();
        var problemReports = await db.ProblemReportLinks.AsNoTracking()
            .Where(x => x.ArtifactType == "TestChangeRequest" && reviewIds.Contains(x.ArtifactId))
            .Select(x => new { x.ArtifactId, x.ProblemReportId }).ToListAsync(ct);
        var impactRows = await db.VerificationImpactItems.AsNoTracking()
            .Where(x => reviewIds.Contains(x.TestChangeReviewId)).ToListAsync(ct);
        var rewritten = new Dictionary<Guid, (Guid ReviewId, string OldHash, string NewHash)>();
        foreach (var review in reviews)
        {
            var problemReportIds = problemReports.Where(x => x.ArtifactId == review.Id)
                .Select(x => x.ProblemReportId).ToList();
            var impactDecisions = impactRows.Where(x => x.TestChangeReviewId == review.Id)
                .Select(x => new VerificationImpactSnapshot(
                    x.Id, x.ChangeRequestId, x.Trigger, x.RequirementChangeId, x.RequirementRevisionId,
                    x.ProcedureId, x.SubjectDisplayNumber, x.Outcome, x.ProcedureChangeAction,
                    x.ResolutionRationale, x.ResolvedProcedureId, x.ResolvedProcedureRevisionId,
                    x.RetargetedRequirementRevisionId, x.PreReleaseEvidenceRequired)).ToList();
            var cycle = review.ReviewCycles.OrderBy(x => x.Sequence).LastOrDefault();
            if (cycle is null || review.State is not (TestChangeReviewState.InReview or TestChangeReviewState.Approved)
                || cycle.State is not (ReviewCycleState.Active or ReviewCycleState.Approved)) continue;
            var replacementHash = review.ComputeSnapshotHashForIdentityMigration(problemReportIds, impactDecisions);
            if (string.Equals(cycle.SnapshotHash, replacementHash, StringComparison.OrdinalIgnoreCase)) continue;
            var oldHash = cycle.SnapshotHash;
            cycle.RecordVerificationIdentityMigration(replacementHash);
            rewritten[cycle.Id] = (review.Id, oldHash, replacementHash);
            db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                "VerificationIdentityMigration.ReviewCycleSnapshotRewritten", Actor,
                $"ReviewCycle:{cycle.Id}", "Succeeded", Json(new
                {
                    migration = MigrationMarker, reviewId = review.Id, reviewCycleId = cycle.Id,
                    oldSnapshotHash = oldHash, newSnapshotHash = replacementHash,
                    reason = "Recomputed through TestChangeReview canonical snapshot authority; prior cycles remain immutable evidence."
                }), "", now));
        }
        return rewritten;
    }

    private async Task RecomputeProcedureManifestsAsync(
        IReadOnlySet<Guid> affectedBaselineIds, DateTimeOffset now, CancellationToken ct)
    {
        var baselines = await db.CandidateBaselines
            .Where(x => x.TestProceduresMaterializedAt != null && affectedBaselineIds.Contains(x.Id))
            .ToListAsync(ct);
        foreach (var baseline in baselines)
        {
            var entries = await (from member in db.BaselineTestProcedures.AsNoTracking()
                                  where member.BaselineId == baseline.Id
                                  join revision in db.TestProcedureRevisions.AsNoTracking()
                                      on member.RevisionId equals revision.Id
                                  join procedure in db.TestProcedures.AsNoTracking().Where(x => x.Level == TestProcedureLevel.System || x.ArtifactKind == VerificationArtifactKind.Case)
                                      on member.ProcedureId equals procedure.Id
                                  select new TestProcedureManifestEntry(procedure.Id, revision.Id,
                                      procedure.BaseNumber, revision.Revision)).ToListAsync(ct);
            baseline.RecordVerificationIdentityMigration(Actor, TestProcedureManifest.Hash(entries), now);
        }
    }

    private async Task QueueAffectedDocumentSignatureSupersessionsAsync(
        IReadOnlyList<ControlledDocument> documents,
        IReadOnlyList<ControlledDocumentArtifact> artifacts,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var documentIds = documents.Select(x => x.Id).ToList();
        var artifactIds = artifacts.Select(x => x.Id).ToList();
        if (documentIds.Count == 0 && artifactIds.Count == 0) return;
        var signatures = await db.ElectronicSignatures.AsNoTracking()
            .Where(x => x.ArtifactType == "ControlledDocument" && documentIds.Contains(x.ArtifactId)
                || x.ArtifactType == "ControlledDocumentArtifact" && artifactIds.Contains(x.ArtifactId))
            .ToListAsync(ct);
        var targets = signatures.Select(x => $"ElectronicSignature:{x.Id}").ToList();
        var alreadyPending = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == "VerificationIdentityMigration.SignatureSuperseded"
                && x.Detail.Contains(MigrationMarker)
                && targets.Contains(x.Target))
            .Select(x => x.Target).ToListAsync(ct);
        foreach (var signature in signatures.Where(x => !alreadyPending.Contains($"ElectronicSignature:{x.Id}")))
        {
            db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                "VerificationIdentityMigration.SignatureSuperseded", Actor,
                $"ElectronicSignature:{signature.Id}", "Superseded",
                Json(new
                {
                    migration = MigrationMarker,
                    oldArtifactIdentity = signature.ArtifactRevision,
                    oldSignatureId = signature.Id,
                    oldSignatureHash = signature.ContentHash,
                    newArtifactIdentity = signature.ArtifactRevision
                        .Replace("SYSTCR-", "SYSTPCR-", StringComparison.Ordinal)
                        .Replace("HLRTCR-", "HLRTCCR-", StringComparison.Ordinal)
                        .Replace("LLRTCR-", "LLRTCCR-", StringComparison.Ordinal),
                    newContentHash = (string?)null,
                    reason = "Affected controlled document evidence must be regenerated or have its exact content basis refreshed by the governed migration authority; no signature was fabricated."
                }), "", now));
        }
    }

    private async Task<int> CompleteSignatureSupersessionsAsync(
        IReadOnlyDictionary<Guid, (string OldHash, string NewHash)> renditionByArtifactId,
            IReadOnlyDictionary<Guid, (string OldHash, string NewHash)> renditionByDocumentId,
        IReadOnlyDictionary<Guid, (Guid ReviewId, string OldHash, string NewHash)> reviewCycleRenditions,
        DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.SecurityAuditEvents
            .Where(x => x.EventType == "VerificationIdentityMigration.SignatureSuperseded"
                && x.Detail.Contains(MigrationMarker))
            .ToListAsync(ct);
        var completed = 0;
        foreach (var evidence in pending)
        {
            var detail = JsonNode.Parse(evidence.Detail)?.AsObject()
                ?? throw new InvalidOperationException($"Signature migration evidence {evidence.Id} is not valid structured JSON.");
            var signatureId = ParseSignatureTarget(evidence.Target);
            var signature = await db.ElectronicSignatures.AsNoTracking().SingleOrDefaultAsync(x => x.Id == signatureId, ct)
                ?? throw new InvalidOperationException($"Signature migration evidence {evidence.Id} has no source signature {signatureId}.");
            string replacementHash;
            var eventType = "VerificationIdentityMigration.SignatureSupersessionCompleted";
            var reason = "The canonical TestChangeRequest snapshot was rewritten by the governed migration authority; the original human signature row and hash remain unchanged and require a new human signature.";
            if (signature.ArtifactType.Equals("TestChangeRequest", StringComparison.OrdinalIgnoreCase))
            {
                var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
                    .Include(x => x.AdditionalSources)
                    .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                    .SingleOrDefaultAsync(x => x.Id == signature.ArtifactId, ct)
                    ?? throw new InvalidOperationException($"Signature {signature.Id} references missing TestChangeRequest {signature.ArtifactId}.");
                var cycle = review.ReviewCycles.OrderBy(x => x.Sequence).LastOrDefault()
                    ?? throw new InvalidOperationException($"Signature {signature.Id} references a TestChangeRequest with no review cycle.");
                var latestCycle = cycle;
                if (signature.ReviewCycle is { } signedCycleSequence)
                {
                    cycle = review.ReviewCycles.SingleOrDefault(x => x.Sequence == signedCycleSequence)
                        ?? throw new InvalidOperationException($"Signature {signature.Id} references review cycle {signedCycleSequence}, which is not owned by its TestChangeRequest.");
                    if (cycle.Id != latestCycle.Id
                        || cycle.State is not (ReviewCycleState.Active or ReviewCycleState.Approved))
                        throw new InvalidOperationException($"Signature {signature.Id} does not belong to the latest reconstructible review cycle.");
                }
                if (signature.ReviewStepId is { } reviewStepId)
                {
                    var signedStep = cycle.Steps.SingleOrDefault(x => x.Id == reviewStepId)
                        ?? throw new InvalidOperationException($"Signature {signature.Id} approval step {reviewStepId} is not owned by review cycle {cycle.Id}.");
                    if (signedStep.State != ApprovalStepState.Approved)
                        throw new InvalidOperationException($"Signature {signature.Id} approval step {reviewStepId} is not an approved step in cycle {cycle.Id}.");
                }
                if (!reviewCycleRenditions.TryGetValue(cycle.Id, out var rendition))
                    throw new InvalidOperationException($"Signature {signature.Id} targets review cycle {cycle.Id}, whose renamed canonical snapshot cannot be reconstructed safely.");
                // The replacement authority is tied to this exact cycle, step, and old hash. A signature
                // from an earlier cycle must remain immutable evidence even when its old hash happens to
                // equal the latest cycle's hash.
                if (!string.Equals(rendition.OldHash, signature.ContentHash, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(cycle.SnapshotHash, signature.ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Signature {signature.Id} does not match the exact latest review-cycle snapshot hash.");
                replacementHash = rendition.NewHash;
                if (string.Equals(replacementHash, signature.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    eventType = "VerificationIdentityMigration.SignatureHashVerified";
                    reason = "The canonical TestChangeRequest snapshot remained byte-identical after the identity migration.";
                }
            }
            else if (signature.ArtifactType.Equals("ControlledDocumentArtifact", StringComparison.OrdinalIgnoreCase)
                && renditionByArtifactId.TryGetValue(signature.ArtifactId, out var artifactRendition))
            {
                if (!string.Equals(signature.ContentHash, artifactRendition.OldHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Signature {signature.Id} does not match the exact pre-migration stored artifact hash.");
                replacementHash = artifactRendition.NewHash;
                reason = "The exact stored controlled rendition bytes were regenerated by the governed migration authority; the original human signature row and hash remain unchanged and require a new human signature.";
            }
            else if (signature.ArtifactType.Equals("ControlledDocument", StringComparison.OrdinalIgnoreCase)
                && renditionByDocumentId.TryGetValue(signature.ArtifactId, out var documentRendition))
            {
                if (!string.Equals(signature.ContentHash, documentRendition.OldHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Signature {signature.Id} does not match the exact pre-migration controlled document content-basis hash.");
                replacementHash = documentRendition.NewHash;
                reason = "The exact controlled document content basis changed, including for an on-demand document without a stored rendition; the original human signature row and hash remain unchanged and require a new human signature.";
            }
            else
                throw new InvalidOperationException($"Signature {signature.Id} ({signature.ArtifactType}/{signature.ArtifactId}) has no exact replacement hash authority.");

            db.SecurityAuditEvents.Add(new SecurityAuditEvent(
                eventType, Actor, evidence.Target, "Succeeded", Json(new
                {
                    migration = MigrationMarker, pendingEvidenceId = evidence.Id, signatureId = signature.Id,
                    oldArtifactIdentity = signature.ArtifactRevision, oldSignatureHash = signature.ContentHash,
                    newArtifactIdentity = detail["newArtifactIdentity"]?.GetValue<string>(),
                    newContentHash = replacementHash, reason
                }), "", now));
            completed++;
        }
        return completed;
    }

    private static Guid ParseSignatureTarget(string target)
    {
        var value = target.StartsWith("ElectronicSignature:", StringComparison.Ordinal)
            ? target["ElectronicSignature:".Length..] : "";
        return Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id : throw new InvalidOperationException($"Signature migration target '{target}' is not a valid ElectronicSignature identity.");
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
