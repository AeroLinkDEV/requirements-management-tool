using System.Data;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public interface IControlledEditingAdapter
{
    ControlledArtifactFamily Family { get; }
    string Name { get; }
    Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct);
    string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null);
    Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor,
        DateTimeOffset now, CancellationToken ct);
}

public sealed record ControlledEditingArtifact(Guid ProjectId, string LifecycleState, object Aggregate,
    long Version, string? Revision);

public enum ControlledCheckInStatus { Succeeded, NotFound, Forbidden, Conflict, InvalidDraft }

public sealed record ControlledCheckInResult(ControlledCheckInStatus Status, string Code, string? Error = null,
    long? ResultingArtifactVersion = null, string? ResultingHash = null, Guid? EvidenceId = null,
    string? Revision = null)
{
    public bool Success => Status == ControlledCheckInStatus.Succeeded;
}

public sealed class ControlledEditingCheckInEngine(
    AeroLinkDbContext db,
    IdentityService identity,
    IEnumerable<IControlledEditingAdapter> adapters)
{
    private readonly IReadOnlyDictionary<ControlledArtifactFamily, IControlledEditingAdapter> _adapters =
        adapters.ToDictionary(x => x.Family);

    public async Task<ControlledCheckInResult> CheckInAsync(Guid sessionId, long expectedVersion,
        AuthenticatedUser actor, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var session = await db.ArtifactEditSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.IsExclusive, ct);
        if (session is null)
            return new(ControlledCheckInStatus.NotFound, "edit_session_not_found", "The controlled edit session was not found.");

        if (session.State != EditSessionState.Active)
            return await RejectAsync(session, actor.UserName, now, "edit_session_inactive",
                "This controlled edit session is no longer active.", ControlledCheckInStatus.Conflict,
                null, null, transaction, ct);
        if (!string.Equals(session.UserName, actor.UserName, StringComparison.OrdinalIgnoreCase))
            return await RejectAsync(session, actor.UserName, now, "edit_session_owner_mismatch",
                "The controlled edit session belongs to another user.", ControlledCheckInStatus.Forbidden,
                null, null, transaction, ct);

        var programId = await db.Projects.Where(x => x.Id == session.ProjectId)
            .Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        var hasProjectAccess = programId is not null &&
            (actor.IsAdministrator || actor.Programs.Any(x => x.ProgramId == programId));
        if (!hasProjectAccess || programId is null ||
            !await HasEditingAuthorityAsync(actor, programId.Value, now, ct))
            return await RejectAsync(session, actor.UserName, now, "project_authorization_required",
                "The user is not authorized to check in controlled content for this project.",
                ControlledCheckInStatus.Forbidden, null, null, transaction, ct);

        if (!ControlledArtifactEditPolicies.TryResolve(session.ArtifactType, out var policy) ||
            !_adapters.TryGetValue(policy.Family, out var adapter))
            return await RejectAsync(session, actor.UserName, now, "check_in_adapter_missing",
                "No controlled check-in adapter is registered for this artifact family.",
                ControlledCheckInStatus.Conflict, null, null, transaction, ct);

        var artifact = await adapter.ResolveAsync(session.ArtifactId, ct);
        if (artifact is null || artifact.ProjectId != session.ProjectId)
            return await RejectAsync(session, actor.UserName, now, "controlled_artifact_not_found",
                "The authoritative controlled artifact could not be resolved.", ControlledCheckInStatus.NotFound,
                adapter, null, transaction, ct);
        if (!policy.IsEditableState(artifact.LifecycleState))
            return await RejectAsync(session, actor.UserName, now, "artifact_not_editable",
                $"{policy.CanonicalType} is in {artifact.LifecycleState} and cannot be checked in.",
                ControlledCheckInStatus.Conflict, adapter, artifact, transaction, ct);
        if (session.ExpiresAt <= now)
        {
            session.Expire(now);
            return await RejectAsync(session, actor.UserName, now, "edit_session_expired",
                "The controlled edit-session lease expired before check-in.", ControlledCheckInStatus.Conflict,
                adapter, artifact, transaction, ct);
        }
        if (session.Version != expectedVersion)
            return await RejectAsync(session, actor.UserName, now, "edit_session_version_mismatch",
                "The editing session changed; refresh before checking in.", ControlledCheckInStatus.Conflict,
                adapter, artifact, transaction, ct);

        var canonicalSnapshot = adapter.CanonicalSnapshot(artifact);
        var canonicalHash = Hash(canonicalSnapshot);
        if (!string.Equals(canonicalHash, session.BaseSnapshotHash, StringComparison.OrdinalIgnoreCase))
            return await RejectAsync(session, actor.UserName, now, "stale_artifact_version",
                "The authoritative artifact changed after checkout. Refresh before checking in.",
                ControlledCheckInStatus.Conflict, adapter, artifact, transaction, ct);

        var draft = await db.ArtifactDraftSnapshots.AsNoTracking()
            .Where(x => x.SessionId == session.Id)
            .OrderByDescending(x => x.Sequence)
            .FirstOrDefaultAsync(ct);
        if (draft is null)
            return await RejectAsync(session, actor.UserName, now, "autosaved_draft_missing",
                "No recoverable autosaved draft exists for this edit session.",
                ControlledCheckInStatus.InvalidDraft, adapter, artifact, transaction, ct);

        try
        {
            using var parsed = JsonDocument.Parse(draft.DraftJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("The latest autosaved draft must be a JSON object.");
        }
        catch (JsonException ex)
        {
            return await RejectAsync(session, actor.UserName, now, "malformed_draft_json",
                ex.Message, ControlledCheckInStatus.InvalidDraft, adapter, artifact, transaction, ct, draft);
        }

        try
        {
            await adapter.ApplyDraftAsync(artifact, draft.DraftJson, actor.UserName, now, ct);
            var resultingVersion = artifact.Version + 1;
            var resultingSnapshot = adapter.CanonicalSnapshot(artifact, resultingVersion);
            var resultingHash = Hash(resultingSnapshot);
            var evidence = Evidence(session, adapter, artifact, actor.UserName, now,
                ControlledCheckInOutcome.Succeeded, "check_in_succeeded", draft, resultingVersion,
                resultingHash, artifact.Revision);
            db.ControlledArtifactCheckInEvidence.Add(evidence);
            db.AuditEvents.Add(new AuditEvent(session.ArtifactId, "ArtifactCheckedIn", actor.UserName,
                JsonSerializer.Serialize(new { evidenceId = evidence.Id, sessionId = session.Id,
                    sessionVersion = session.Version, adapter = adapter.Name, session.BaseSnapshotHash,
                    resultingSnapshotHash = resultingHash, aggregateVersionBefore = artifact.Version,
                    aggregateVersionAfter = resultingVersion, revisionBefore = artifact.Revision,
                    revisionAfter = artifact.Revision }), now));
            session.Close(EditSessionState.Committed, expectedVersion, now, actor.UserName,
                $"Checked in through {adapter.Name}; evidence {evidence.Id}.");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(ControlledCheckInStatus.Succeeded, "check_in_succeeded",
                ResultingArtifactVersion: resultingVersion, ResultingHash: resultingHash,
                EvidenceId: evidence.Id, Revision: artifact.Revision);
        }
        catch (JsonException ex)
        {
            await transaction.RollbackAsync(ct);
            return await PersistRejectedAfterRollbackAsync(sessionId, actor.UserName, now,
                "malformed_draft_json", ex.Message, ControlledCheckInStatus.InvalidDraft, adapter.Name, ct);
        }
        catch (DomainException ex)
        {
            await transaction.RollbackAsync(ct);
            return await PersistRejectedAfterRollbackAsync(sessionId, actor.UserName, now,
                "aggregate_validation_failed", ex.Message, ControlledCheckInStatus.InvalidDraft, adapter.Name, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            return await PersistRejectedAfterRollbackAsync(sessionId, actor.UserName, now,
                "stale_artifact_version", "The authoritative artifact changed during check-in.",
                ControlledCheckInStatus.Conflict, adapter.Name, ct);
        }
    }

    private async Task<bool> HasEditingAuthorityAsync(AuthenticatedUser actor, Guid programId,
        DateTimeOffset now, CancellationToken ct)
    {
        foreach (var role in new[] { ProgramRole.Engineer, ProgramRole.TestEngineer,
                     ProgramRole.ConfigurationManager, ProgramRole.ProgramManager })
            if (await identity.HasRoleAsync(actor, programId, role, now, ct)) return true;
        return false;
    }

    private async Task<ControlledCheckInResult> RejectAsync(ArtifactEditSession session, string actor,
        DateTimeOffset now, string code, string error, ControlledCheckInStatus status,
        IControlledEditingAdapter? adapter, ControlledEditingArtifact? artifact,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken ct,
        ArtifactDraftSnapshot? draft = null)
    {
        var evidence = Evidence(session, adapter, artifact, actor, now, ControlledCheckInOutcome.Failed,
            $"{code}: {error}", draft);
        db.ControlledArtifactCheckInEvidence.Add(evidence);
        db.AuditEvents.Add(new AuditEvent(session.ArtifactId, "ArtifactCheckInRejected", actor,
            JsonSerializer.Serialize(new { evidenceId = evidence.Id, sessionId = session.Id, code, error,
                adapter = adapter?.Name ?? "Unavailable", sessionVersion = session.Version,
                session.BaseSnapshotHash, aggregateVersion = artifact?.Version }), now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(status, code, error, EvidenceId: evidence.Id);
    }

    private async Task<ControlledCheckInResult> PersistRejectedAfterRollbackAsync(Guid sessionId,
        string actor, DateTimeOffset now, string code, string error, ControlledCheckInStatus status,
        string adapterName, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var session = await db.ArtifactEditSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId, ct);
        var evidence = new ControlledArtifactCheckInEvidence(session.ProjectId, session.ArtifactType,
            session.ArtifactId, adapterName, actor, now, session.Id, session.Version,
            session.BaseSnapshotHash, null, 0, null, null, null, null, null,
            ControlledCheckInOutcome.Failed, $"{code}: {error}");
        db.ControlledArtifactCheckInEvidence.Add(evidence);
        db.AuditEvents.Add(new AuditEvent(session.ArtifactId, "ArtifactCheckInRejected", actor,
            JsonSerializer.Serialize(new { evidenceId = evidence.Id, sessionId, code, error,
                adapter = adapterName, sessionVersion = session.Version, session.BaseSnapshotHash }), now));
        await db.SaveChangesAsync(ct);
        return new(status, code, error, EvidenceId: evidence.Id);
    }

    private static ControlledArtifactCheckInEvidence Evidence(ArtifactEditSession session,
        IControlledEditingAdapter? adapter, ControlledEditingArtifact? artifact, string actor,
        DateTimeOffset now, ControlledCheckInOutcome outcome, string reason,
        ArtifactDraftSnapshot? draft = null, long? versionAfter = null,
        string? resultingHash = null, string? revisionAfter = null) =>
        new(session.ProjectId, session.ArtifactType, session.ArtifactId, adapter?.Name ?? "Unavailable",
            actor, now, session.Id, session.Version, session.BaseSnapshotHash, resultingHash,
            artifact?.Version ?? 0, versionAfter, artifact?.Revision, revisionAfter,
            draft?.Id, draft?.Sequence, outcome, reason);

    private static string Hash(string value) => EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(value));
}

public sealed class SystemChangeRequestControlledEditingAdapter(AeroLinkDbContext db) : IControlledEditingAdapter
{
    private static readonly JsonSerializerOptions DraftOptions = new() { PropertyNameCaseInsensitive = true };
    public ControlledArtifactFamily Family => ControlledArtifactFamily.ChangeRequest;
    public string Name => "SystemChangeRequestControlledEditingAdapter";

    public async Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct)
    {
        var item = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .SingleOrDefaultAsync(x => x.Id == artifactId, ct);
        return item is null ? null : new(item.ProjectId, item.State.ToString(), item, item.Version,
            item.Revision.ToString());
    }

    public string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null)
    {
        var item = (SystemChangeRequest)artifact.Aggregate;
        return Snapshot(item, versionOverride);
    }

    public static string Snapshot(SystemChangeRequest item, long? versionOverride = null) =>
        JsonSerializer.Serialize(new { scrVersion = versionOverride ?? item.Version, title = item.Title,
            problem = item.Problem, analysis = item.Analysis, solution = item.Solution,
            requirementChanges = item.RequirementChanges.Select(x => new { baseNumber = x.BaseNumber,
                revision = x.Revision, level = x.Level.ToString(), kind = x.Kind.ToString(),
                statement = x.Statement, rationale = x.Rationale, verificationMethod = x.VerificationMethod,
                richText = x.RichText, attributesJson = x.AttributesJson,
                impactDispositionJson = x.ImpactDispositionJson }) });

    public async Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor,
        DateTimeOffset now, CancellationToken ct)
    {
        var item = (SystemChangeRequest)artifact.Aggregate;
        var draft = JsonSerializer.Deserialize<SystemChangeRequestDraft>(draftJson, DraftOptions)
            ?? throw new JsonException("The latest autosaved SCR draft is empty.");
        if (draft.RequirementChanges is null)
            throw new JsonException("The latest autosaved SCR draft does not contain requirement changes.");
        var changes = await NormalizeAsync(item, draft.RequirementChanges, ct);
        item.UpdateDraft(actor, draft.Title ?? "", draft.Problem ?? "", draft.Analysis ?? "",
            draft.Solution ?? "", changes, now);
    }

    private async Task<IReadOnlyList<RequirementChangeDraft>> NormalizeAsync(SystemChangeRequest scr,
        IReadOnlyList<SystemChangeRequestRequirementDraft> requested, CancellationToken ct)
    {
        var existing = scr.RequirementChanges.ToDictionary(x => x.BaseNumber, StringComparer.OrdinalIgnoreCase);
        var nextNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<RequirementChangeDraft>(requested.Count);
        foreach (var raw in requested)
        {
            if (!Enum.TryParse<RequirementLevel>(raw.Level, true, out var level) ||
                !Enum.TryParse<RequirementChangeKind>(raw.Kind, true, out var kind))
                throw new DomainException("Every requirement change needs a valid level and change kind.");
            if (scr.Type == ChangeRequestType.System && level != RequirementLevel.System)
                throw new DomainException("A System SCR can contain only System requirement changes.");
            if (scr.Type == ChangeRequestType.Software && level == RequirementLevel.System)
                throw new DomainException("A Software SWCR can contain only HLR and LLR changes.");
            if (raw.IsDerived && string.IsNullOrWhiteSpace(raw.Rationale))
                throw new DomainException("Every derived software requirement requires an explicit engineering rationale.");

            var supplied = (raw.BaseNumber ?? "").Trim().ToUpperInvariant();
            string baseNumber; int revision;
            if (existing.TryGetValue(supplied, out var preserved))
            {
                if (raw.Revision != preserved.Revision || level != preserved.Level || kind != preserved.Kind)
                    throw new DomainException($"The controlled identity of {preserved.DisplayNumber} cannot change.");
                baseNumber = preserved.BaseNumber; revision = preserved.Revision;
            }
            else if (kind == RequirementChangeKind.Introduce)
            {
                var prefix = level switch { RequirementLevel.System => "SYSR", RequirementLevel.HighLevel => "HLR", _ => "LLR" };
                if (!nextNumbers.TryGetValue(prefix, out var next)) next = await NextSequenceAsync(prefix, ct);
                baseNumber = $"{prefix}-{next:D6}"; nextNumbers[prefix] = next + 1; revision = 0;
            }
            else
            {
                var requirement = await db.Requirements.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ProjectId == scr.ProjectId && x.BaseNumber == supplied, ct);
                if (requirement is null)
                    throw new DomainException($"Select an existing controlled requirement before proposing a {kind.ToString().ToLowerInvariant()}.");
                if (requirement.Level != level)
                    throw new DomainException($"{requirement.BaseNumber} is not a {level} requirement.");
                baseNumber = requirement.BaseNumber;
                revision = await db.RequirementRevisions.Where(x => x.ArtifactId == requirement.Id)
                    .MaxAsync(x => x.Revision, ct) + 1;
            }
            if (!reserved.Add(baseNumber))
                throw new DomainException($"{baseNumber} appears more than once in this Draft.");
            normalized.Add(new(baseNumber, revision, level, kind, raw.Statement ?? "", raw.Rationale ?? "",
                raw.VerificationMethod ?? "", raw.RichText ?? "", raw.AttributesJson ?? "{}",
                raw.ImpactDispositionJson ?? "{}"));
        }
        return normalized;
    }

    private async Task<int> NextSequenceAsync(string prefix, CancellationToken ct)
    {
        var values = await db.Requirements.AsNoTracking().Where(x => x.BaseNumber.StartsWith(prefix + "-"))
            .Select(x => x.BaseNumber).Concat(db.RequirementChanges.AsNoTracking()
                .Where(x => x.BaseNumber.StartsWith(prefix + "-")).Select(x => x.BaseNumber)).ToListAsync(ct);
        return values.Select(x => int.TryParse(x[(x.LastIndexOf('-') + 1)..], out var value) ? value : 0)
            .DefaultIfEmpty(0).Max() + 1;
    }

    private sealed record SystemChangeRequestDraft(string? Title, string? Problem, string? Analysis,
        string? Solution, List<SystemChangeRequestRequirementDraft>? RequirementChanges);
    private sealed record SystemChangeRequestRequirementDraft(string? BaseNumber, int Revision,
        string? Level, string? Kind, string? Statement, string? Rationale, string? VerificationMethod,
        string? RichText, string? AttributesJson, string? ImpactDispositionJson, bool IsDerived = false);
}
