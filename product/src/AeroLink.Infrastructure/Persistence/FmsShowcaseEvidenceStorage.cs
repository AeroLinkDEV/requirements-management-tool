using System.Text.Json;
using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed partial class FmsShowcaseSeeder
{
    private const string ShowcaseEvidenceStoragePrefix = "active-verification-913/storage/";
    private sealed record ShowcaseEvidenceStorage(Guid EvidenceId, StagedEvidence File);
    private readonly List<ShowcaseEvidenceStorage> stagedUpgradeEvidence = [];

    private async Task<EvidenceRecord> StageShowcaseEvidenceAsync(Guid programId, Guid projectId,
        byte[] bytes, string actor, DateTimeOffset at, CancellationToken ct)
    {
        if (evidenceStore is null) throw new InvalidOperationException("An explicit showcase evidence store is required.");
        using var stream = new MemoryStream(bytes);
        var staged = await evidenceStore.StageAsync(stream, Guid.NewGuid(), "showcase",
            "fms-1.6-synthetic-verification-fixture.json", "application/json", ct);
        var evidence = new EvidenceRecord(projectId, staged.OriginalFileName, staged.ContentType, staged.Size,
            staged.Sha256, staged.StorageKey, actor, at);
        var intent = new ShowcaseEvidenceStorage(evidence.Id, staged);
        stagedUpgradeEvidence.Add(intent);
        db.EvidenceRecords.Add(evidence);
        // This durable identity commits with the evidence and executions. If promotion is interrupted
        // after commit, the next supported upgrade can verify and promote the exact staged bytes.
        db.ShowcaseUpgradeSteps.Add(new(programId, ShowcaseEvidenceStoragePrefix + evidence.Id.ToString("D"),
            JsonSerializer.Serialize(intent), at));
        return evidence;
    }

    private async Task PromoteCommittedShowcaseEvidenceAsync(Guid programId)
    {
        var markers = await db.ShowcaseUpgradeSteps.AsNoTracking().Where(x => x.ProgramId == programId
            && x.StepKey.StartsWith(ShowcaseEvidenceStoragePrefix)).ToListAsync(CancellationToken.None);
        foreach (var marker in markers)
        {
            if (evidenceStore is null) throw new InvalidOperationException("Pending showcase evidence requires its explicit evidence store.");
            var intent = JsonSerializer.Deserialize<ShowcaseEvidenceStorage>(marker.Detail)
                ?? throw new InvalidOperationException("Invalid showcase evidence storage identity.");
            var evidence = await db.EvidenceRecords.AsNoTracking().SingleAsync(x => x.Id == intent.EvidenceId, CancellationToken.None);
            var projectProgram = await db.Projects.AsNoTracking().Where(x => x.Id == evidence.ProjectId)
                .Select(x => x.ProgramId).SingleAsync(CancellationToken.None);
            var file = intent.File;
            if (projectProgram != programId || marker.StepKey != ShowcaseEvidenceStoragePrefix + evidence.Id.ToString("D")
                || evidence.StorageKey != file.StorageKey || evidence.Sha256 != file.Sha256
                || evidence.Size != file.Size || evidence.OriginalFileName != file.OriginalFileName || evidence.ContentType != file.ContentType)
                throw new InvalidOperationException("The exact committed showcase evidence storage identity has drifted.");
            // Request cancellation must not interrupt completion of an already committed operation.
            // PromoteAsync also verifies an already promoted object, making retries idempotent.
            await evidenceStore.PromoteAsync(file, CancellationToken.None);
        }
    }

    private async Task DiscardUncommittedShowcaseEvidenceAsync()
    {
        foreach (var intent in stagedUpgradeEvidence)
        {
            // Called only after the database transaction has been disposed. Check durable state even
            // when a commit threw: losing its acknowledgement is not proof the commit rolled back.
            // On an unavailable database retain staging and surface the failure; never delete uncertain
            // committed evidence. No final object is promoted before a confirmed successful commit.
            if (!await db.EvidenceRecords.AsNoTracking().AnyAsync(x => x.Id == intent.EvidenceId, CancellationToken.None))
                evidenceStore!.Delete(intent.File.StagingKey);
        }
    }
}
