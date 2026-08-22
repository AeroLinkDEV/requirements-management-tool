using System.Data;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public interface IControlledEditingAdapter
{
    ControlledArtifactFamily Family { get; }
    string Name { get; }
    Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct);
    string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null);
    Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor,
        bool administratorAuthority, DateTimeOffset now, CancellationToken ct);
}

public sealed record ControlledEditingArtifact(Guid ProjectId, string LifecycleState, object Aggregate,
    long Version, string? Revision, Guid? AuditAggregateId);

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
    IEnumerable<IControlledEditingAdapter> adapters,
    ILadderPolicy? policy = null)
{
    private readonly ILadderPolicy ladderPolicy = policy ?? LegacyLadderPolicy.Instance;
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
            await adapter.ApplyDraftAsync(artifact, draft.DraftJson, actor.UserName, actor.IsAdministrator, now, ct);
            var resultingVersion = artifact.Version + 1;
            var resultingSnapshot = adapter.CanonicalSnapshot(artifact, resultingVersion);
            var resultingHash = Hash(resultingSnapshot);
            var evidence = Evidence(session, adapter, artifact, actor.UserName, now,
                ControlledCheckInOutcome.Succeeded, "check_in_succeeded", draft, resultingVersion,
                resultingHash, artifact.Revision);
            db.ControlledArtifactCheckInEvidence.Add(evidence);
            AddLegacyAudit(artifact.AuditAggregateId, "ArtifactCheckedIn", actor.UserName,
                $"Checked in the controlled edit, taking the record to version {resultingVersion}.", now,
                JsonSerializer.Serialize(new { evidenceId = evidence.Id, sessionId = session.Id,
                    sessionVersion = session.Version, adapter = adapter.Name, session.BaseSnapshotHash,
                    resultingSnapshotHash = resultingHash, aggregateVersionBefore = artifact.Version,
                    aggregateVersionAfter = resultingVersion, revisionBefore = artifact.Revision,
                    revisionAfter = artifact.Revision }));
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
                "malformed_draft_json", ex.Message, ControlledCheckInStatus.InvalidDraft, adapter.Name,
                artifact.AuditAggregateId, ct);
        }
        catch (DomainException ex)
        {
            await transaction.RollbackAsync(ct);
            return await PersistRejectedAfterRollbackAsync(sessionId, actor.UserName, now,
                "aggregate_validation_failed", ex.Message, ControlledCheckInStatus.InvalidDraft, adapter.Name,
                artifact.AuditAggregateId, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            return await PersistRejectedAfterRollbackAsync(sessionId, actor.UserName, now,
                "stale_artifact_version", "The authoritative artifact changed during check-in.",
                ControlledCheckInStatus.Conflict, adapter.Name, artifact.AuditAggregateId, ct);
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
        if (artifact is not null)
            AddLegacyAudit(artifact.AuditAggregateId, "ArtifactCheckInRejected", actor,
            JsonSerializer.Serialize(new { evidenceId = evidence.Id, sessionId = session.Id, code, error,
                adapter = adapter?.Name ?? "Unavailable", sessionVersion = session.Version,
                session.BaseSnapshotHash, aggregateVersion = artifact.Version }), now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(status, code, error, EvidenceId: evidence.Id);
    }

    private async Task<ControlledCheckInResult> PersistRejectedAfterRollbackAsync(Guid sessionId,
        string actor, DateTimeOffset now, string code, string error, ControlledCheckInStatus status,
        string adapterName, Guid? auditAggregateId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var session = await db.ArtifactEditSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId, ct);
        var evidence = new ControlledArtifactCheckInEvidence(session.ProjectId, session.ArtifactType,
            session.ArtifactId, adapterName, actor, now, session.Id, session.Version,
            session.BaseSnapshotHash, null, 0, null, null, null, null, null,
            ControlledCheckInOutcome.Failed, $"{code}: {error}");
        db.ControlledArtifactCheckInEvidence.Add(evidence);
        AddLegacyAudit(auditAggregateId, "ArtifactCheckInRejected", actor,
            JsonSerializer.Serialize(new { evidenceId = evidence.Id, sessionId, code, error,
                adapter = adapterName, sessionVersion = session.Version, session.BaseSnapshotHash }), now);
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

    private void AddLegacyAudit(Guid? aggregateId, string eventType, string actor, string detail, DateTimeOffset now,
        string? evidenceJson = null)
    {
        // The legacy audit_events table is scoped to SystemChangeRequest. Universal evidence is
        // the authoritative audit record for every other artifact family.
        //
        // `detail` is the sentence a reader sees; `evidenceJson` is the structured record behind it. They used
        // to be one field, which is how a serialized payload of GUIDs and hashes became the audit narrative.
        if (aggregateId is not null) db.AuditEvents.Add(new AuditEvent(aggregateId.Value, eventType, actor, detail, now, evidenceJson));
    }
}

public sealed class SystemChangeRequestControlledEditingAdapter(AeroLinkDbContext db, ILadderPolicy? policy = null,
    IProjectLadderPolicyResolver? policyResolver = null) : IControlledEditingAdapter
{
    private readonly ILadderPolicy ladderPolicy = policy ?? LegacyLadderPolicy.Instance;
    private static readonly JsonSerializerOptions DraftOptions = new() { PropertyNameCaseInsensitive = true };
    public ControlledArtifactFamily Family => ControlledArtifactFamily.ChangeRequest;
    public string Name => "SystemChangeRequestControlledEditingAdapter";

    public async Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct)
    {
        var item = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .SingleOrDefaultAsync(x => x.Id == artifactId, ct);
        if (item is null) return null;
        var reportIds = await db.ProblemReportLinks.AsNoTracking().Where(link => link.ArtifactType == "ChangeRequest"
            && link.ArtifactId == item.Id && link.Relationship == ProblemReportRelationshipPolicy.ProposedCorrectiveAction)
            .Select(link => link.ProblemReportId).OrderBy(id => id).ToListAsync(ct);
        return new(item.ProjectId, item.State.ToString(), new State(item, reportIds), item.Version,
            item.Revision.ToString(), item.Id);
    }

    public string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null)
    {
        var state = (State)artifact.Aggregate;
        return Snapshot(state.Request, state.ProblemReportIds, versionOverride);
    }

    public static string Snapshot(SystemChangeRequest item, IReadOnlyList<Guid>? problemReportIds = null,
        long? versionOverride = null) =>
        JsonSerializer.Serialize(new { scrVersion = versionOverride ?? item.Version, title = item.Title,
            problem = item.Problem, analysis = item.Analysis, solution = item.Solution,
            problemRich = item.ProblemRich, analysisRich = item.AnalysisRich, solutionRich = item.SolutionRich,
            problemReportIds = problemReportIds?.OrderBy(id => id),
            requirementChanges = item.RequirementChanges.Select(x => new { baseNumber = x.BaseNumber,
                revision = x.Revision, level = x.Level.ToString(), kind = x.Kind.ToString(),
                statement = x.Statement, rationale = x.Rationale, verificationMethod = x.VerificationMethod,
                richText = x.RichText, attributesJson = x.AttributesJson,
                impactDispositionJson = x.ImpactDispositionJson, targetSectionId = x.TargetSectionId,
                upstreamRevisionIds = ProposedParents(x.ProposedUpstreamRevisionIdsJson) }) });

    public async Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor,
        bool administratorAuthority, DateTimeOffset now, CancellationToken ct)
    {
        var state = (State)artifact.Aggregate;
        var item = state.Request;
        var draft = JsonSerializer.Deserialize<SystemChangeRequestDraft>(draftJson, DraftOptions)
            ?? throw new JsonException("The latest autosaved change request draft is empty.");
        if (draft.RequirementChanges is null)
            throw new JsonException("The latest autosaved change request draft does not contain requirement changes.");
        var effectivePolicy = policyResolver is null ? ladderPolicy : await policyResolver.ResolveAsync(item.ProjectId, ct);
        var changes = await NormalizeAsync(item, draft.RequirementChanges, effectivePolicy, ct);
        // Check-in is an author putting work down, not submitting it. A proposal they started and were
        // interrupted in is stored as it stands; SystemChangeRequest.ValidateReadyForReview refuses it by
        // name when the Draft is offered to an approver.
        item.UpdateDraft(actor, draft.Title ?? "", draft.Problem ?? "", draft.Analysis ?? "",
            draft.Solution ?? "", changes, now, draft.ProblemRich, draft.AnalysisRich, draft.SolutionRich,
            administratorAuthority, allowIncomplete: true, ladderPolicy: effectivePolicy);
        var selectedReports = (draft.ProblemReportIds ?? state.ProblemReportIds)
            .Distinct().OrderBy(id => id).ToList();
        await new ProblemReportLinkService(db).ReplaceDraftChangeRequestLinksAsync(item, selectedReports,
            actor, now, ct);
        state.ProblemReportIds.Clear();
        state.ProblemReportIds.AddRange(selectedReports);
    }

    private async Task<IReadOnlyList<RequirementChangeDraft>> NormalizeAsync(SystemChangeRequest scr,
        IReadOnlyList<SystemChangeRequestRequirementDraft> requested, ILadderPolicy policy, CancellationToken ct)
    {
        // Schema/specification catalogue synchronization and authored identity resolve the same effective
        // project policy, so a check-in cannot silently restore an absent level.
        await new EnterpriseRequirementsService(db, ladderPolicy, policyResolver).SynchronizeProjectAsync(scr.ProjectId, scr.AuthorId, ct);
        var existing = scr.RequirementChanges.ToDictionary(x => x.BaseNumber, StringComparer.OrdinalIgnoreCase);
        var nextNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<RequirementChangeDraft>(requested.Count);
        foreach (var raw in requested)
        {
            if (!Enum.TryParse<RequirementLevel>(raw.Level, true, out var level) ||
                !Enum.TryParse<RequirementChangeKind>(raw.Kind, true, out var kind))
                throw new DomainException("Every requirement change needs a valid level and change kind.");
            if (!policy.AcceptsChangeRequest(scr.Type, level))
                throw new DomainException(scr.Type == ChangeRequestType.System
                    ? "A System change request can contain only System requirement changes."
                    : scr.Type == ChangeRequestType.Interface
                        ? "An Interface change request can contain only Interface requirement changes."
                    : "A Software change request can contain only HLR and LLR changes.");
            var definition = policy.Definition(level);
            var hasRequirementsDocument = definition.Has(LevelCapabilities.HasRequirementsDocument)
                && definition.RequirementsCatalogue is not null;
            if (!hasRequirementsDocument && raw.TargetSectionId is not null)
                throw new DomainException($"The configured {level} level has no requirements document section to receive a change.");
            var isDerived = raw.IsDerived ?? RequirementAuthoringJson.IsDerived(raw.AttributesJson);
            if (isDerived && string.IsNullOrWhiteSpace(raw.Rationale))
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
                var prefix = policy.RequirementPrefix(level);
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
            var attributes = "{}";
            if (hasRequirementsDocument)
            {
                var schema = await db.ArtifactSchemas.Include(x => x.Fields).SingleOrDefaultAsync(x =>
                    x.ProjectId == scr.ProjectId && x.IsActive && x.AppliesTo == level.ToString(), ct)
                    ?? throw new DomainException($"No active requirement schema is configured for {level}.");
                attributes = RequirementAuthoringJson.ValidateAndMergeAttributes(raw.AttributesJson, schema,
                    policy.IsDownstreamTarget(level) && isDerived);
            }
            normalized.Add(new(baseNumber, revision, level, kind, raw.Statement ?? "", raw.Rationale ?? "",
                raw.VerificationMethod ?? "", raw.RichText ?? "", attributes,
                raw.ImpactDispositionJson ?? "{}", raw.TargetSectionId,
                JsonSerializer.Serialize(raw.UpstreamRevisionIds ?? ProposedParents(preserved?.ProposedUpstreamRevisionIdsJson))));
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
        string? Solution, List<SystemChangeRequestRequirementDraft>? RequirementChanges,
        string? ProblemRich = null, string? AnalysisRich = null, string? SolutionRich = null,
        List<Guid>? ProblemReportIds = null);
    private sealed record State(SystemChangeRequest Request, List<Guid> ProblemReportIds);
    private sealed record SystemChangeRequestRequirementDraft(string? BaseNumber, int Revision,
        string? Level, string? Kind, string? Statement, string? Rationale, string? VerificationMethod,
        string? RichText, string? AttributesJson, string? ImpactDispositionJson, bool? IsDerived = null,
        Guid? TargetSectionId = null, List<Guid>? UpstreamRevisionIds = null);

    private static IReadOnlyList<Guid> ProposedParents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}

public sealed class RequirementProposalControlledEditingAdapter(AeroLinkDbContext db, ILadderPolicy? policy = null,
    IProjectLadderPolicyResolver? policyResolver = null) : IControlledEditingAdapter
{
    private readonly ILadderPolicy ladderPolicy = policy ?? LegacyLadderPolicy.Instance;
    private static readonly JsonSerializerOptions DraftOptions = new() { PropertyNameCaseInsensitive = true };
    public ControlledArtifactFamily Family => ControlledArtifactFamily.RequirementProposal;
    public string Name => "RequirementProposalControlledEditingAdapter";

    public async Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct)
    {
        var parent = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .SingleOrDefaultAsync(x => x.RequirementChanges.Any(change => change.Id == artifactId), ct);
        if (parent is null) return null;
        var proposal = parent.RequirementChanges.Single(x => x.Id == artifactId);
        return new(parent.ProjectId, parent.State.ToString(),
            new State(parent, artifactId, proposal.BaseNumber, proposal.Revision), parent.Version,
            proposal.Revision.ToString(), parent.Id);
    }

    public string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null)
    {
        var state = (State)artifact.Aggregate;
        return Snapshot(FindProposal(state),
            versionOverride ?? state.Parent.Version);
    }

    public static string Snapshot(RequirementChange item, long parentVersion) =>
        JsonSerializer.Serialize(new { item.Id, item.BaseNumber, item.Revision,
            level = item.Level.ToString(), kind = item.Kind.ToString(), item.Statement, item.Rationale,
            item.VerificationMethod, item.RichText, item.AttributesJson, item.ImpactDispositionJson,
            targetSectionId = item.TargetSectionId,
            upstreamRevisionIds = ProposedParents(item.ProposedUpstreamRevisionIdsJson), parentVersion });

    public async Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor,
        bool administratorAuthority, DateTimeOffset now, CancellationToken ct)
    {
        var state = (State)artifact.Aggregate;
        var parent = state.Parent;
        var effectivePolicy = policyResolver is null ? ladderPolicy : await policyResolver.ResolveAsync(parent.ProjectId, ct);
        var current = parent.RequirementChanges.Single(x => x.Id == state.ProposalId);
        var draft = JsonSerializer.Deserialize<ProposalDraft>(draftJson, DraftOptions)
            ?? throw new JsonException("The latest autosaved requirement proposal is empty.");
        if (!string.Equals(draft.BaseNumber?.Trim(), current.BaseNumber, StringComparison.OrdinalIgnoreCase) ||
            draft.Revision != current.Revision ||
            !Enum.TryParse<RequirementLevel>(draft.Level, true, out var level) || level != current.Level ||
            !Enum.TryParse<RequirementChangeKind>(draft.Kind, true, out var kind) || kind != current.Kind)
            throw new DomainException($"The controlled identity of {current.DisplayNumber} cannot change.");
        var definition = effectivePolicy.Definition(current.Level);
        var hasRequirementsDocument = definition.Has(LevelCapabilities.HasRequirementsDocument)
            && definition.RequirementsCatalogue is not null;
        if (!hasRequirementsDocument && (draft.TargetSectionId ?? current.TargetSectionId) is not null)
            throw new DomainException($"The configured {current.Level} level has no requirements document section to receive a change.");
        var attributes = "{}";
        if (hasRequirementsDocument)
        {
            // Synchronize the catalogue from the effective project policy before validating the proposal's
            // structured attributes; the draft cannot author against an absent or capability-disabled level.
            await new EnterpriseRequirementsService(db, ladderPolicy, policyResolver).SynchronizeProjectAsync(parent.ProjectId, actor, ct);
            var schema = await db.ArtifactSchemas.Include(x => x.Fields).SingleOrDefaultAsync(x =>
                x.ProjectId == parent.ProjectId && x.IsActive && x.AppliesTo == current.Level.ToString(), ct)
                ?? throw new DomainException($"No active requirement schema is configured for {current.Level}.");
            attributes = RequirementAuthoringJson.ValidateAndMergeAttributes(draft.AttributesJson, schema,
                effectivePolicy.IsDownstreamTarget(current.Level) &&
                (draft.IsDerived ?? RequirementAuthoringJson.IsDerived(current.AttributesJson)));
        }

        // Every proposal is rewritten from these drafts, so anything not carried here is lost. The chosen section
        // of the untouched proposals comes from the stored change, and of the edited one from the draft — falling
        // back to what was stored, because a draft written before the field existed does not mean "no section".
        var changes = parent.RequirementChanges.Select(item => item.Id == current.Id
            ? new RequirementChangeDraft(current.BaseNumber, current.Revision, current.Level, current.Kind,
                draft.Statement ?? "", draft.Rationale ?? "", draft.VerificationMethod ?? "",
                draft.RichText ?? "", attributes, draft.ImpactDispositionJson ?? "{}",
                draft.TargetSectionId ?? current.TargetSectionId,
                JsonSerializer.Serialize(draft.UpstreamRevisionIds ?? ProposedParents(current.ProposedUpstreamRevisionIdsJson)))
            : new RequirementChangeDraft(item.BaseNumber, item.Revision, item.Level, item.Kind, item.Statement,
                item.Rationale, item.VerificationMethod, item.RichText, item.AttributesJson,
                item.ImpactDispositionJson, item.TargetSectionId, item.ProposedUpstreamRevisionIdsJson)).ToList();
        parent.UpdateDraft(actor, parent.Title, parent.Problem, parent.Analysis, parent.Solution, changes, now,
            parent.ProblemRich, parent.AnalysisRich, parent.SolutionRich, ladderPolicy: effectivePolicy);
    }

    private static RequirementChange FindProposal(State state) => state.Parent.RequirementChanges.Single(x =>
        x.Id == state.ProposalId ||
        (x.BaseNumber.Equals(state.BaseNumber, StringComparison.OrdinalIgnoreCase) && x.Revision == state.Revision));

    private sealed record State(SystemChangeRequest Parent, Guid ProposalId, string BaseNumber, int Revision);
    /// <param name="TargetSectionId">
    /// The section chosen for this requirement. Null in a draft written before the field existed, and in that case
    /// the stored value is kept rather than cleared — check-in replaces the whole proposal set, so a field the
    /// draft does not mention would otherwise be erased by saving an unrelated edit.
    /// </param>
    private sealed record ProposalDraft(string? BaseNumber, int Revision, string? Level, string? Kind,
        string? Statement, string? Rationale, string? VerificationMethod, string? RichText,
        string? AttributesJson, string? ImpactDispositionJson, Guid? TargetSectionId = null,
        bool? IsDerived = null, List<Guid>? UpstreamRevisionIds = null);

    private static IReadOnlyList<Guid> ProposedParents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}

public sealed class SpecificationStructureControlledEditingAdapter(AeroLinkDbContext db,
    IProjectLadderPolicyResolver? policyResolver = null, ILadderPolicy? policy = null) : IControlledEditingAdapter
{
    private readonly ILadderPolicy fallbackPolicy = policy ?? LegacyLadderPolicy.Instance;
    private static readonly JsonSerializerOptions DraftOptions = new() { PropertyNameCaseInsensitive = true };
    public ControlledArtifactFamily Family => ControlledArtifactFamily.SpecificationStructure;
    public string Name => "SpecificationStructureControlledEditingAdapter";

    public async Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct)
    {
        var specification = await db.RequirementSpecifications.SingleOrDefaultAsync(x => x.Id == artifactId, ct);
        if (specification is null)
        {
            var node = await db.SpecificationNodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
            if (node is null) return null;
            specification = await db.RequirementSpecifications.SingleOrDefaultAsync(x => x.Id == node.SpecificationId, ct);
        }
        if (specification is null) return null;
        var effectivePolicy = policyResolver is null
            ? fallbackPolicy
            : await policyResolver.ResolveAsync(specification.ProjectId, ct);
        var isCurrent = specification.IsActive && effectivePolicy.Definitions.Any(definition =>
            definition.Has(LevelCapabilities.HasRequirementsDocument)
            && definition.RequirementsCatalogue is not null
            && definition.Level.ToString() == specification.Level
            && definition.RequirementsCatalogue.SpecificationNumber == specification.DocumentNumber);
        if (!isCurrent) return null;
        var nodes = await db.SpecificationNodes.Where(x => x.SpecificationId == specification.Id).ToListAsync(ct);
        return new(specification.ProjectId, "InWork", new State(specification, nodes), specification.Version, null, null);
    }

    public string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null)
    {
        var state = (State)artifact.Aggregate;
        return Snapshot(state.Specification, state.Nodes, versionOverride);
    }

    public static string Snapshot(RequirementSpecification specification, IEnumerable<SpecificationNode> nodes,
        long? versionOverride = null) => JsonSerializer.Serialize(new
    {
        id = specification.Id, specification.DocumentNumber, specification.Title, specification.Level,
        specification.Description, version = versionOverride ?? specification.Version,
        nodes = nodes.OrderBy(x => x.ParentId).ThenBy(x => x.Position).ThenBy(x => x.Id).Select(x => new
        {
            x.Id, x.ParentId, x.Position, type = x.Type.ToString(), x.Heading, x.RequirementArtifactId
        })
    });

    public Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor,
        bool administratorAuthority, DateTimeOffset now, CancellationToken ct)
    {
        var state = (State)artifact.Aggregate;
        var draft = JsonSerializer.Deserialize<SpecificationDraft>(draftJson, DraftOptions)
            ?? throw new JsonException("The latest autosaved specification draft is empty.");
        var specification = state.Specification;
        if (draft.Id != specification.Id || !string.Equals(draft.DocumentNumber?.Trim(), specification.DocumentNumber, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The controlled specification identity cannot change.");
        if (draft.Nodes is null) throw new JsonException("The latest autosaved specification draft does not contain nodes.");
        var existing = state.Nodes.ToDictionary(x => x.Id);
        if (draft.Nodes.Count != existing.Count || draft.Nodes.Any(x => !existing.ContainsKey(x.Id)))
            throw new DomainException("Adding or removing specification nodes requires the controlled structural authoring operation.");
        if (draft.Nodes.GroupBy(x => new { x.ParentId, x.Position }).Any(x => x.Count() > 1))
            throw new DomainException("Specification node positions must be unique within each parent section.");
        var ids = draft.Nodes.Select(x => x.Id).ToHashSet();
        if (draft.Nodes.Any(x => x.ParentId is not null && !ids.Contains(x.ParentId.Value)))
            throw new DomainException("Every specification node parent must remain within the same specification.");
        foreach (var item in draft.Nodes)
        {
            var node = existing[item.Id];
            if (!Enum.TryParse<SpecificationNodeType>(item.Type, true, out var type) || type != node.Type ||
                item.RequirementArtifactId != node.RequirementArtifactId)
                throw new DomainException("The controlled specification node identity cannot change.");
            node.UpdateDraft(item.ParentId, item.Position, item.Heading ?? string.Empty, actor, now);
        }
        EnsureAcyclic(draft.Nodes);
        specification.UpdateDraft(draft.Title ?? string.Empty, draft.Level ?? string.Empty, draft.Description ?? string.Empty, actor, now);
        specification.RecordStructureUpdate(actor, now);
        return Task.CompletedTask;
    }

    private static void EnsureAcyclic(IEnumerable<SpecificationNodeDraft> nodes)
    {
        var parents = nodes.ToDictionary(x => x.Id, x => x.ParentId);
        foreach (var node in parents.Keys)
        {
            var visited = new HashSet<Guid> { node };
            var current = parents[node];
            while (current is not null)
            {
                if (!visited.Add(current.Value)) throw new DomainException("A specification structure cannot contain a parent cycle.");
                current = parents[current.Value];
            }
        }
    }

    private sealed record State(RequirementSpecification Specification, List<SpecificationNode> Nodes);
    private sealed record SpecificationDraft(Guid Id, string? DocumentNumber, string? Title, string? Level,
        string? Description, long Version, List<SpecificationNodeDraft>? Nodes);
    private sealed record SpecificationNodeDraft(Guid Id, Guid? ParentId, int Position, string? Type,
        string? Heading, Guid? RequirementArtifactId);
}

public sealed class TraceLinkProposalControlledEditingAdapter(AeroLinkDbContext db,
    IProjectLadderPolicyResolver? policyResolver = null, ILadderPolicy? policy = null) : IControlledEditingAdapter
{
    private readonly ILadderPolicy ladderPolicy = policy ?? LegacyLadderPolicy.Instance;
    private static readonly JsonSerializerOptions DraftOptions = new() { PropertyNameCaseInsensitive = true };
    public ControlledArtifactFamily Family => ControlledArtifactFamily.TraceLinkProposal;
    public string Name => "TraceLinkProposalControlledEditingAdapter";

    public async Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct)
    {
        var item = await db.RequirementTraces.SingleOrDefaultAsync(x => x.Id == artifactId, ct);
        return item is null ? null : new(item.ProjectId, "Proposed", item, item.Version, null, null);
    }

    public string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null) =>
        Snapshot((RequirementTraceLink)artifact.Aggregate, versionOverride);

    public static string Snapshot(RequirementTraceLink item, long? versionOverride = null) => JsonSerializer.Serialize(new
    {
        item.Id, item.ProjectId, item.SourceRevisionId, item.TargetRevisionId, type = item.Type.ToString(),
        item.Rationale, version = versionOverride ?? item.Version
    });

    public async Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor,
        bool administratorAuthority, DateTimeOffset now, CancellationToken ct)
    {
        var item = (RequirementTraceLink)artifact.Aggregate;
        var draft = JsonSerializer.Deserialize<TraceLinkDraft>(draftJson, DraftOptions)
            ?? throw new JsonException("The latest autosaved trace-link draft is empty.");
        if (draft.Id != item.Id || draft.ProjectId != item.ProjectId || draft.SourceRevisionId != item.SourceRevisionId ||
            draft.TargetRevisionId != item.TargetRevisionId || !Enum.TryParse<RequirementTraceType>(draft.Type, true, out var type))
            throw new DomainException("The controlled trace-link identity cannot change.");
        var effectivePolicy = policyResolver is null ? ladderPolicy : await policyResolver.ResolveAsync(item.ProjectId, ct);
        var levels = await (from revision in db.RequirementRevisions.AsNoTracking()
                            join requirement in db.Requirements.AsNoTracking() on revision.ArtifactId equals requirement.Id
                            where (revision.Id == item.SourceRevisionId || revision.Id == item.TargetRevisionId)
                                && requirement.ProjectId == item.ProjectId
                            select new { revision.Id, requirement.Level }).ToListAsync(ct);
        if (levels.Count != 2) throw new DomainException("Both exact requirement revisions must exist before a trace can be changed.");
        RequirementTracePolicy.Validate(effectivePolicy, levels.Single(x => x.Id == item.SourceRevisionId).Level,
            levels.Single(x => x.Id == item.TargetRevisionId).Level, type);
        if (await db.RequirementTraces.AsNoTracking().AnyAsync(x => x.Id != item.Id &&
                x.SourceRevisionId == item.SourceRevisionId && x.TargetRevisionId == item.TargetRevisionId && x.Type == type, ct))
            throw new DomainException("An identical controlled trace link already exists.");
        item.UpdateProposal(type, draft.Rationale ?? string.Empty, now);
    }

    private sealed record TraceLinkDraft(Guid Id, Guid ProjectId, Guid SourceRevisionId, Guid TargetRevisionId,
        string? Type, string? Rationale, long Version);
}

public sealed class ReleasePlanningControlledEditingAdapter(AeroLinkDbContext db) : IControlledEditingAdapter
{
    private static readonly JsonSerializerOptions DraftOptions = new() { PropertyNameCaseInsensitive = true };
    public ControlledArtifactFamily Family => ControlledArtifactFamily.ReleasePlanning;
    public string Name => "ReleasePlanningControlledEditingAdapter";

    public async Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct)
    {
        var item = await db.CandidateBaselines.Include(x => x.Selections).SingleOrDefaultAsync(x => x.Id == artifactId, ct);
        return item is null ? null : new(item.ProjectId, item.State.ToString(), item, item.Version,
            item.Revision.ToString(), null);
    }

    public string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null) =>
        Snapshot((CandidateBaseline)artifact.Aggregate, versionOverride);

    public static string Snapshot(CandidateBaseline item, long? versionOverride = null) => JsonSerializer.Serialize(new
    {
        item.Id, item.BaseNumber, item.Revision, item.Name, item.ReleaseId, item.PredecessorBaselineId,
        state = item.State.ToString(), item.ContentHash, item.RequirementsHash, version = versionOverride ?? item.Version,
        selectedScrIds = item.Selections.OrderBy(x => x.ChangeRequestDisplayNumber).Select(x => x.ChangeRequestId)
    });

    public async Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor,
        bool administratorAuthority, DateTimeOffset now, CancellationToken ct)
    {
        var baseline = (CandidateBaseline)artifact.Aggregate;
        var draft = JsonSerializer.Deserialize<ReleasePlanningDraft>(draftJson, DraftOptions)
            ?? throw new JsonException("The latest autosaved release-planning draft is empty.");
        if (draft.Id != baseline.Id || draft.ReleaseId != baseline.ReleaseId || draft.Revision != baseline.Revision ||
            draft.PredecessorBaselineId != baseline.PredecessorBaselineId ||
            !string.Equals(draft.BaseNumber?.Trim(), baseline.BaseNumber, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The controlled release-planning identity cannot change.");
        if (draft.SelectedScrIds is null || draft.SelectedScrIds.Count != draft.SelectedScrIds.Distinct().Count())
            throw new DomainException("Release-planning selections must contain distinct change request identifiers.");
        var requested = draft.SelectedScrIds.ToHashSet();
        var existing = baseline.Selections.Select(x => x.ChangeRequestId).ToHashSet();
        var allIds = requested.Union(existing).ToList();
        var scrs = await db.SystemChangeRequests.Where(x => allIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        if (scrs.Count != allIds.Count) throw new DomainException("Every selected release-planning change request must exist.");
        foreach (var id in existing.Except(requested).ToList()) baseline.Remove(scrs[id], actor, now);
        foreach (var id in requested.Except(existing).ToList()) baseline.Select(scrs[id], actor, now);
        baseline.UpdateDraft(draft.Name ?? string.Empty, actor, now);
    }

    private sealed record ReleasePlanningDraft(Guid Id, string? BaseNumber, int Revision, string? Name,
        Guid ReleaseId, Guid? PredecessorBaselineId, string? State, string? ContentHash, string? RequirementsHash,
        long Version, List<Guid>? SelectedScrIds);
}

public sealed class DocumentTemplateControlledEditingAdapter(AeroLinkDbContext db) : IControlledEditingAdapter
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public ControlledArtifactFamily Family => ControlledArtifactFamily.DocumentTemplate;
    public string Name => "DocumentTemplateControlledEditingAdapter";
    public async Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct)
    {
        var item = await db.DocumentTemplates.SingleOrDefaultAsync(x => x.Id == artifactId, ct);
        return item is null ? null : new(item.ProjectId, item.State.ToString(), item, item.Version, null, null);
    }
    public string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null) => Snapshot((DocumentTemplate)artifact.Aggregate, versionOverride);
    public static string Snapshot(DocumentTemplate item, long? versionOverride = null) => JsonSerializer.Serialize(new { item.Id, item.ProjectId, item.TemplateNumber, item.Title, item.Body, item.OwnerId, state = item.State.ToString(), version = versionOverride ?? item.Version });
    public Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor, bool administratorAuthority, DateTimeOffset now, CancellationToken ct)
    {
        var item = (DocumentTemplate)artifact.Aggregate; var draft = JsonSerializer.Deserialize<TemplateDraft>(draftJson, Options) ?? throw new JsonException("The latest document-template draft is empty.");
        if (draft.Id != item.Id || draft.ProjectId != item.ProjectId || !string.Equals(draft.TemplateNumber?.Trim(), item.TemplateNumber, StringComparison.OrdinalIgnoreCase)) throw new DomainException("The controlled document-template identity cannot change.");
        item.UpdateDraft(draft.Title ?? "", draft.Body ?? "", draft.OwnerId ?? "", now); return Task.CompletedTask;
    }
    private sealed record TemplateDraft(Guid Id, Guid ProjectId, string? TemplateNumber, string? Title, string? Body, string? OwnerId, string? State, long Version);
}

public sealed class ProblemReportControlledEditingAdapter(AeroLinkDbContext db) : IControlledEditingAdapter
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public ControlledArtifactFamily Family => ControlledArtifactFamily.ProblemReport;
    public string Name => "ProblemReportControlledEditingAdapter";
    public async Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct)
    {
        var item = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == artifactId, ct);
        // No audit aggregate: AuditEvent.AggregateId is a foreign key to a change request. A Problem
        // Report's controlled history is its own ProblemReportRevision chain, written on check-in below.
        return item is null ? null : new(item.ProjectId, item.State.ToString(), item, item.Version, null, null);
    }
    public string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null) => Snapshot((ProblemReport)artifact.Aggregate, versionOverride);
    /// <summary>
    /// The working copy a checkout hands to the editor, and the shape a draft must come back in.
    ///
    /// Every field the editor may change is here, because a working copy that omits one silently reverts it
    /// on check-in. The identity fields — report number, project and who raised it — are here to be checked,
    /// not to be changed.
    /// </summary>
    // Every name is written out in camelCase rather than left to shorthand. The working copy is read by the
    // browser editor, and a snapshot that mixed PascalCase shorthand with explicitly-named camelCase members
    // handed the client a document where half the fields were invisible to it.
    public static string Snapshot(ProblemReport item, long? versionOverride = null) =>
        ProblemReportEvidenceContract.Serialize(item, versionOverride);
    /// <summary>
    /// The immutable lifecycle evidence written for the report's History. Shared with the lifecycle
    /// endpoints so a correction made under checkout is recorded exactly like every other change, rather
    /// than in a shape of its own that a reader would have to interpret differently.
    /// </summary>
    public static string EvidenceSnapshot(ProblemReport report) => ProblemReportEvidenceContract.Serialize(report);
    public async Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor, bool administratorAuthority, DateTimeOffset now, CancellationToken ct)
    {
        var item = (ProblemReport)artifact.Aggregate; var draft = JsonSerializer.Deserialize<ProblemDraft>(draftJson, Options) ?? throw new JsonException("The latest problem-report draft is empty.");
        // Identity is checked, never applied. The report number, its project, who raised it and who is
        // responsible for it are facts about the record, not fields on the form.
        if (draft.Id != item.Id || draft.ProjectId != item.ProjectId
            || !string.Equals(draft.ReportNumber?.Trim(), item.ReportNumber, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(draft.ReportedBy?.Trim(), item.ReportedBy, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(draft.ResponsibleEngineerId?.Trim() ?? item.ResponsibleEngineerId, item.ResponsibleEngineerId, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The controlled problem-report identity cannot change.");
        var fromState = ProblemReportTransitionPolicy.Canonical(item.State);
        var wasAwaitingClosure = fromState == ProblemReportState.WaitingForSqaToClose;
        item.UpdateDetails(administratorAuthority ? item.ResponsibleEngineerId : actor,
            draft.Title ?? "", draft.Problem ?? "", draft.ProblemRich ?? "",
            draft.AdditionalInformation ?? "", draft.AdditionalInformationRich ?? "", draft.Analysis ?? "",
            draft.RootCause ?? "", draft.CorrectiveAction ?? "", draft.SystemAircraftImpact ?? "",
            draft.ImpactAssessmentJson ?? "", ParseEnum(draft.Severity, item.Severity), ParseEnum(draft.Priority, item.Priority), now,
            ParseEnum(draft.Type, item.Type), draft.Workaround);
        var toState = ProblemReportTransitionPolicy.Canonical(item.State);
        var lifecycleRationale = fromState != toState
            ? "Controlled detail correction invalidated the prior closure evidence and returned the report to Verifying."
            : null;
        db.ProblemReportRevisions.Add(new ProblemReportRevision(item.Id, item.Revision, "DetailsCheckedIn",
            actor, item.CanonicalHash(), EvidenceSnapshot(item), now,
            detail: lifecycleRationale, fromState: fromState.ToString(), toState: toState.ToString(), rationale: lifecycleRationale));
        if (wasAwaitingClosure)
            await new ProblemReportClosureCandidateService(db).InvalidatePendingAsync(item, actor,
                "DetailsCheckedIn", now, ct, fromState, toState, lifecycleRationale);
    }
    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
    private sealed record ProblemDraft(Guid Id, Guid ProjectId, string? ReportNumber, string? Title, string? Problem,
        string? ProblemRich, string? AdditionalInformation, string? AdditionalInformationRich, string? Analysis,
        string? RootCause, string? CorrectiveAction, string? SystemAircraftImpact, string? ImpactAssessmentJson,
        string? Severity, string? Priority, string? ReportedBy, string? ResponsibleEngineerId, string? State, long Version,
        string? Type = null, string? Workaround = null);
}

public sealed class ConfigurationChangeSetControlledEditingAdapter(AeroLinkDbContext db) : IControlledEditingAdapter
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public ControlledArtifactFamily Family => ControlledArtifactFamily.ConfigurationChangeSet;
    public string Name => "ConfigurationChangeSetControlledEditingAdapter";
    public async Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct)
    {
        var item = await db.ConfigurationChangeSets.SingleOrDefaultAsync(x => x.Id == artifactId, ct);
        return item is null ? null : new(item.ProjectId, item.State.ToString(), item, item.Version, null, null);
    }
    public string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null) => Snapshot((ConfigurationChangeSet)artifact.Aggregate, versionOverride);
    public static string Snapshot(ConfigurationChangeSet item, long? versionOverride = null) => JsonSerializer.Serialize(new { item.Id, item.ProjectId, item.ChangeSetNumber, item.Title, item.Description, item.OwnerId, state = item.State.ToString(), version = versionOverride ?? item.Version });
    public Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor, bool administratorAuthority, DateTimeOffset now, CancellationToken ct)
    {
        var item = (ConfigurationChangeSet)artifact.Aggregate; var draft = JsonSerializer.Deserialize<ChangeSetDraft>(draftJson, Options) ?? throw new JsonException("The latest configuration change-set draft is empty.");
        if (draft.Id != item.Id || draft.ProjectId != item.ProjectId || !string.Equals(draft.ChangeSetNumber?.Trim(), item.ChangeSetNumber, StringComparison.OrdinalIgnoreCase)) throw new DomainException("The controlled configuration change-set identity cannot change.");
        item.UpdateDraft(draft.Title ?? "", draft.Description ?? "", draft.OwnerId ?? "", now); return Task.CompletedTask;
    }
    private sealed record ChangeSetDraft(Guid Id, Guid ProjectId, string? ChangeSetNumber, string? Title, string? Description, string? OwnerId, string? State, long Version);
}

/// <summary>
/// Checking out a test change request, the way a change request is checked out.
///
/// The package is the record that governs procedure change (DEC-103), so it is the record that gets a working
/// copy — a test procedure itself has no editing path and deliberately never gains one.
///
/// The draft carries the engineering case and the procedure changes together, exactly as the change request's
/// draft carries its case and its requirement changes: an engineer correcting a package is usually correcting
/// both, and a check-in that applied only half of what they wrote would silently discard the other half.
/// </summary>
public sealed class TestChangeRequestControlledEditingAdapter(AeroLinkDbContext db,
    IProjectLadderPolicyResolver? policyResolver = null) : IControlledEditingAdapter
{
    private static readonly JsonSerializerOptions DraftOptions = new() { PropertyNameCaseInsensitive = true };
    public ControlledArtifactFamily Family => ControlledArtifactFamily.TestChangeRequest;
    public string Name => "TestChangeRequestControlledEditingAdapter";

    public async Task<ControlledEditingArtifact?> ResolveAsync(Guid artifactId, CancellationToken ct)
    {
        var item = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
            .SingleOrDefaultAsync(x => x.Id == artifactId, ct);
        return item is null
            ? null
            : new(item.ProjectId, item.State.ToString(), item, item.Version, item.Revision.ToString(), null);
    }

    public string CanonicalSnapshot(ControlledEditingArtifact artifact, long? versionOverride = null) =>
        Snapshot((TestChangeReview)artifact.Aggregate, versionOverride);

    public static string Snapshot(TestChangeReview item, long? versionOverride = null) =>
        JsonSerializer.Serialize(new
        {
            packageVersion = versionOverride ?? item.Version,
            title = item.Title,
            problem = item.Problem,
            analysis = item.Analysis,
            solution = item.Solution,
            problemRich = item.ProblemRich,
            analysisRich = item.AnalysisRich,
            solutionRich = item.SolutionRich,
            // Ordered so a snapshot is a function of content and not of the order rows came back in.
            procedureChanges = item.ProcedureChanges
                .OrderBy(x => x.BaseNumber).ThenBy(x => x.Revision)
                .Select(x => new
                {
                    baseNumber = x.BaseNumber, revision = x.Revision, level = x.Level.ToString(),
                    kind = x.Kind.ToString(), title = x.Title, objective = x.Objective,
                    preconditions = x.Preconditions, steps = x.Steps, expectedResult = x.ExpectedResult,
                    rationale = x.Rationale, drivingRequirementRevisionIdsJson = x.DrivingRequirementRevisionIdsJson,
                    removedRequirementRevisionIdsJson = x.RemovedRequirementRevisionIdsJson,
                    coverageChangeRationale = x.CoverageChangeRationale,
                }),
        });

    public async Task ApplyDraftAsync(ControlledEditingArtifact artifact, string draftJson, string actor,
        bool administratorAuthority, DateTimeOffset now, CancellationToken ct)
    {
        var item = (TestChangeReview)artifact.Aggregate;
        var ladderPolicy = policyResolver is null
            ? LegacyLadderPolicy.Instance
            : await policyResolver.ResolveAsync(item.ProjectId, ct);
        var draft = JsonSerializer.Deserialize<TestChangeRequestDraft>(draftJson, DraftOptions)
            ?? throw new JsonException("The latest autosaved test change request draft is empty.");
        if (draft.ProcedureChanges is null)
            throw new JsonException($"The latest autosaved test change request draft does not contain {(item.Discipline == TestChangeReviewDiscipline.System ? "procedure" : "case")} changes.");

        item.WriteCase(actor, draft.Title ?? "", draft.Problem ?? "", draft.Analysis ?? "", draft.Solution ?? "",
            now, draft.ProblemRich, draft.AnalysisRich, draft.SolutionRich);

        // Replaced rather than merged, because the draft is the whole of what the engineer wrote. Merging
        // would leave a proposal they deleted still attached to the package they checked in.
        foreach (var existing in item.ProcedureChanges.ToList()) item.RemoveProcedureChange(existing.Id, now);
        foreach (var change in draft.ProcedureChanges)
            item.AddProcedureChange(actor, new TestProcedureChangeDraft(
                change.BaseNumber ?? "", change.Revision,
                Enum.TryParse<TestProcedureLevel>(change.Level, true, out var level) ? level : TestProcedureLevel.System,
                Enum.TryParse<TestProcedureChangeKind>(change.Kind, true, out var kind) ? kind : TestProcedureChangeKind.Introduce,
                change.Title ?? "", change.Objective ?? "", change.Preconditions ?? "", change.Steps ?? "",
                change.ExpectedResult ?? "", change.Rationale ?? "",
                change.DrivingRequirementRevisionIdsJson ?? "[]", change.RemovedRequirementRevisionIdsJson ?? "[]",
                // As above: a half-written proposal is checked in as it stands, and SubmitForReview is what
                // refuses to show it to an approver.
                 change.CoverageChangeRationale ?? ""), now, allowIncomplete: true, policy: ladderPolicy);
    }

    private sealed record TestChangeRequestDraft(string? Title, string? Problem, string? Analysis, string? Solution,
        string? ProblemRich, string? AnalysisRich, string? SolutionRich,
        IReadOnlyList<TestProcedureChangeDraftPayload>? ProcedureChanges);

    private sealed record TestProcedureChangeDraftPayload(string? BaseNumber, int Revision, string? Level,
        string? Kind, string? Title, string? Objective, string? Preconditions, string? Steps,
        string? ExpectedResult, string? Rationale, string? DrivingRequirementRevisionIdsJson,
        string? RemovedRequirementRevisionIdsJson, string? CoverageChangeRationale);
}
