using System.Data;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record LegacyProcedureManifestBootstrapView(
    Guid BaselineId,
    string BaselineDisplayNumber,
    string ProceduresHash,
    int ActiveProcedureCount,
    int RetiredProcedureCount,
    int DraftRevisionCount,
    string SelectionRule,
    bool AlreadyBootstrapped,
    DateTimeOffset? RecordedAt,
    string? RecordedBy);

/// <summary>
/// Establishes the one exact predecessor manifest a project created before build-scoped procedure membership
/// existed.
///
/// This is deliberately not part of ordinary successor materialization. A migration assertion is an explicit,
/// attributable Configuration Management action; silently using today's inventory while another build is being
/// materialized would make the predecessor appear historically exact when it never was.
/// </summary>
public sealed class LegacyProcedureManifestBootstrapper(AeroLinkDbContext db)
{
    public const string SelectionRule =
        "Latest non-Draft controlled revision for each procedure in the same project; a latest Retired revision suppresses that procedure.";

    private sealed record LegacySnapshot(
        IReadOnlyList<TestProcedureManifestEntry> Active,
        int RetiredProcedureCount,
        int DraftRevisionCount)
    {
        public string Hash => TestProcedureManifest.Hash(Active);
    }

    private sealed record RevisionCandidate(
        Guid ProcedureId,
        Guid RevisionId,
        string BaseNumber,
        int Revision,
        TestProcedureState State,
        DateTimeOffset CreatedAt);

    public async Task<LegacyProcedureManifestBootstrapView?> PreviewAsync(Guid baselineId, CancellationToken ct)
    {
        var baseline = await db.CandidateBaselines.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == baselineId, ct);
        return baseline is null ? null : await PreviewCoreAsync(baseline, ct);
    }

    public async Task<LegacyProcedureManifestBootstrapView?> BootstrapAsync(
        Guid baselineId,
        string actorId,
        string expectedHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new DomainException("A Configuration Manager is required for the legacy procedure bootstrap.");
        if (string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64)
            throw new DomainException("Confirm the exact preview hash before establishing the legacy procedure manifest.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var baseline = await db.CandidateBaselines.Include(x => x.Events)
                .SingleOrDefaultAsync(x => x.Id == baselineId, ct);
            if (baseline is null) return null;

            var preview = await PreviewCoreAsync(baseline, ct);
            if (!string.Equals(preview.ProceduresHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new DomainException(
                    "The legacy procedure inventory changed after preview. Refresh the preview and confirm its new hash before continuing.");

            // A retry after a successful commit is an idempotent read, not a second mutation or a duplicate
            // event. The exact stored membership is re-hashed by PreviewCoreAsync before this can be returned.
            if (preview.AlreadyBootstrapped)
            {
                await transaction.CommitAsync(ct);
                return preview;
            }

            var snapshot = await SnapshotAsync(baseline.ProjectId, ct);
            if (!string.Equals(snapshot.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new DomainException(
                    "The legacy procedure inventory changed while the bootstrap was starting. Refresh and confirm the new preview.");

            foreach (var member in snapshot.Active)
                db.BaselineTestProcedures.Add(
                    new BaselineTestProcedureSelection(baseline.Id, member.ProcedureId, member.RevisionId));

            baseline.BootstrapLegacyTestProcedures(actorId.Trim(), snapshot.Hash, snapshot.Active.Count,
                snapshot.RetiredProcedureCount, SelectionRule, now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new LegacyProcedureManifestBootstrapView(
                baseline.Id, baseline.DisplayNumber, snapshot.Hash, snapshot.Active.Count,
                snapshot.RetiredProcedureCount, snapshot.DraftRevisionCount, SelectionRule, true, now, actorId.Trim());
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();

            // Two Configuration Managers may confirm the same immutable preview together. The unique manifest
            // membership and baseline state allow only one writer; the other request returns the exact result
            // the winner committed when it is the same hash, and otherwise gets a truthful conflict.
            var existing = await PreviewAsync(baselineId, ct);
            if (existing is { AlreadyBootstrapped: true }
                && string.Equals(existing.ProceduresHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                return existing;

            throw new DomainException(
                "Another operation changed the legacy procedure manifest while it was being established. Refresh its current state before retrying.");
        }
    }

    private async Task<LegacyProcedureManifestBootstrapView> PreviewCoreAsync(
        CandidateBaseline baseline,
        CancellationToken ct)
    {
        // SQLite cannot translate DateTimeOffset ordering. Materialize the tiny, baseline-scoped event set and
        // apply the deterministic ordering in memory so SQLite and PostgreSQL tell the same story.
        var bootstrapEvents = await db.BaselineEvents.AsNoTracking()
            .Where(x => x.BaselineId == baseline.Id && x.EventType == "LegacyProcedureManifestBootstrapped")
            .ToListAsync(ct);
        var bootstrapEvent = bootstrapEvents.OrderByDescending(x => x.OccurredAt).FirstOrDefault();

        if (baseline.TestProceduresMaterializedAt is not null)
        {
            if (bootstrapEvent is null)
                throw new DomainException(
                    "This baseline already has an ordinary build-scoped procedure manifest; legacy bootstrap does not apply.");

            var exact = await ExistingManifestAsync(baseline.Id, ct);
            var exactHash = TestProcedureManifest.Hash(exact);
            if (!string.Equals(exactHash, baseline.TestProceduresHash, StringComparison.OrdinalIgnoreCase))
                throw new DomainException(
                    "The stored legacy procedure manifest does not match its recorded hash. Stop and investigate the configuration record.");

            return new LegacyProcedureManifestBootstrapView(
                baseline.Id, baseline.DisplayNumber, exactHash, exact.Count, 0, 0, SelectionRule, true,
                bootstrapEvent.OccurredAt, bootstrapEvent.ActorId);
        }

        await ValidateEligibilityAsync(baseline, ct);
        var snapshot = await SnapshotAsync(baseline.ProjectId, ct);
        return new LegacyProcedureManifestBootstrapView(
            baseline.Id, baseline.DisplayNumber, snapshot.Hash, snapshot.Active.Count,
            snapshot.RetiredProcedureCount, snapshot.DraftRevisionCount, SelectionRule, false, null, null);
    }

    private async Task ValidateEligibilityAsync(CandidateBaseline baseline, CancellationToken ct)
    {
        if (baseline.State == CandidateBaselineState.Draft)
            throw new DomainException("Freeze and materialize the legacy requirement baseline before establishing its procedure snapshot.");
        if (baseline.RequirementsMaterializedAt is null)
            throw new DomainException("The legacy requirement baseline must be materialized before its procedures can be bootstrapped.");

        var released = await db.Releases.AsNoTracking()
            .Where(x => x.Id == baseline.ReleaseId)
            .Select(x => x.IsReleased)
            .SingleAsync(ct);
        if (!released && baseline.State != CandidateBaselineState.Released)
            throw new DomainException(
                "Legacy procedure bootstrap is reserved for a released predecessor, not an in-work baseline. Use ordinary procedure materialization for current work.");

        if (await db.CandidateBaselines.AsNoTracking().AnyAsync(
                x => x.ProjectId == baseline.ProjectId && x.Id != baseline.Id
                     && x.TestProceduresMaterializedAt != null, ct))
            throw new DomainException(
                "This project already has a build-scoped procedure manifest. Legacy bootstrap is available only for the first predecessor snapshot.");

        if (await (from member in db.BaselineTestProcedures.AsNoTracking()
                   join candidate in db.CandidateBaselines.AsNoTracking() on member.BaselineId equals candidate.Id
                   where candidate.ProjectId == baseline.ProjectId
                   select member.Id).AnyAsync(ct))
            throw new DomainException(
                "Procedure-manifest membership already exists without a complete recorded legacy bootstrap. Stop and investigate rather than replacing it.");
    }

    private async Task<LegacySnapshot> SnapshotAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await (from procedure in db.TestProcedures.AsNoTracking()
                          join revision in db.TestProcedureRevisions.AsNoTracking()
                              on procedure.Id equals revision.ProcedureId
                          where procedure.ProjectId == projectId
                          select new RevisionCandidate(procedure.Id, revision.Id, procedure.BaseNumber,
                              revision.Revision, revision.State, revision.CreatedAt)).ToListAsync(ct);

        // Drafts are excluded before choosing the latest controlled candidate. Otherwise a later proposal can
        // hide the still-effective Approved predecessor. Retired remains eligible for that choice because a
        // later retirement must suppress the procedure rather than resurrect the older Approved revision.
        var controlled = rows.Where(x => x.State != TestProcedureState.Draft).ToList();
        var latest = controlled.GroupBy(x => x.ProcedureId)
            .Select(group => group.OrderByDescending(x => x.Revision)
                .ThenByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.RevisionId)
                .First())
            .ToList();
        var active = latest.Where(x => x.State == TestProcedureState.Approved)
            .Select(x => new TestProcedureManifestEntry(
                x.ProcedureId, x.RevisionId, x.BaseNumber, x.Revision))
            .OrderBy(x => x.BaseNumber, StringComparer.Ordinal)
            .ThenBy(x => x.Revision)
            .ThenBy(x => x.RevisionId)
            .ToList();

        return new LegacySnapshot(active,
            latest.Count(x => x.State == TestProcedureState.Retired),
            rows.Count(x => x.State == TestProcedureState.Draft));
    }

    private async Task<List<TestProcedureManifestEntry>> ExistingManifestAsync(Guid baselineId, CancellationToken ct) =>
        await (from member in db.BaselineTestProcedures.AsNoTracking().Where(x => x.BaselineId == baselineId)
               join procedure in db.TestProcedures.AsNoTracking() on member.ProcedureId equals procedure.Id
               join revision in db.TestProcedureRevisions.AsNoTracking() on member.RevisionId equals revision.Id
               orderby procedure.BaseNumber, revision.Revision, revision.Id
               select new TestProcedureManifestEntry(
                   procedure.Id, revision.Id, procedure.BaseNumber, revision.Revision)).ToListAsync(ct);
}
