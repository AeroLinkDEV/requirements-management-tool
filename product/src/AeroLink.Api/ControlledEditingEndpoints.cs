using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public static class ControlledEditingEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkControlledEditingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/controlled-editing");
        group.MapGet("/policies", GetPolicies);
        group.MapGet("/status", GetStatusAsync);
        group.MapPost("/checkout", CheckoutAsync);
        group.MapPut("/sessions/{id:guid}/autosave", AutosaveAsync);
        group.MapPost("/sessions/{id:guid}/heartbeat", HeartbeatAsync);
        group.MapPost("/sessions/{id:guid}/check-in", CheckInAsync);
        group.MapPost("/sessions/{id:guid}/discard", DiscardAsync);
        group.MapPost("/sessions/{id:guid}/force-unlock", ForceUnlockAsync);
        return app;
    }

    private static IResult GetPolicies() => Results.Ok(ControlledArtifactEditPolicies.All.Select(policy => new
    {
        family = policy.Family.ToString(),
        policy.CanonicalType,
        policy.Exclusive,
        policy.DefaultLeaseMinutes,
        policy.MinimumLeaseMinutes,
        policy.MaximumLeaseMinutes,
        editableStates = policy.EditableStates.OrderBy(x => x),
        aliases = policy.Aliases.OrderBy(x => x)
    }));

    private static async Task<IResult> GetStatusAsync(string artifactType, Guid artifactId, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        if (!ControlledArtifactEditPolicies.TryResolve(artifactType, out var policy))
            return Results.BadRequest(new { error = $"'{artifactType}' is not a supported controlled draft artifact type.", code = "unsupported_artifact_type" });

        var artifact = await ResolveAsync(policy, artifactId, db, ct);
        if (artifact is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, artifact.ProjectId, ct)) return Results.Forbid();

        var now = DateTimeOffset.UtcNow;
        var sessions = await db.ArtifactEditSessions
            .Where(x => x.ArtifactId == artifactId && x.ArtifactType == policy.CanonicalType && x.IsExclusive && x.State == EditSessionState.Active)
            .ToListAsync(ct);
        foreach (var expired in sessions.Where(x => x.ExpiresAt <= now)) expired.Expire(now);
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        var active = sessions.FirstOrDefault(x => x.State == EditSessionState.Active);

        return Results.Ok(new
        {
            artifactType = policy.CanonicalType,
            artifactId,
            artifact.State,
            editable = policy.IsEditableState(artifact.State),
            locked = active is not null,
            sessionId = active?.Id,
            holder = active?.UserName,
            openedAt = active?.OpenedAt,
            lastActivityAt = active?.UpdatedAt,
            expiresAt = active?.ExpiresAt,
            mine = active?.UserName == http.UserAccount().UserName,
            adapter = artifact.Adapter
        });
    }

    private static async Task<IResult> CheckoutAsync(UniversalCheckoutRequest request, HttpContext http,
        AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        if (!ControlledArtifactEditPolicies.TryResolve(request.ArtifactType, out var policy))
            return Results.BadRequest(new { error = $"'{request.ArtifactType}' is not a supported controlled draft artifact type.", code = "unsupported_artifact_type" });

        var artifact = await ResolveAsync(policy, request.ArtifactId, db, ct);
        if (artifact is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, artifact.ProjectId, ct)) return Results.Forbid();
        if (!await http.HasProjectRoleAsync(db, identity, artifact.ProjectId, ct,
                ProgramRole.Engineer, ProgramRole.TestEngineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager))
            return Results.Forbid();
        if (!policy.IsEditableState(artifact.State))
            return Results.Conflict(new { error = $"{policy.CanonicalType} is in {artifact.State} and cannot be checked out.", code = "artifact_not_editable" });

        int leaseMinutes;
        try { leaseMinutes = policy.NormalizeLease(request.LeaseMinutes); }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message, code = "invalid_lease" }); }

        var actor = http.UserAccount();
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.ArtifactEditSessions
            .Where(x => x.ArtifactId == request.ArtifactId && x.ArtifactType == policy.CanonicalType && x.IsExclusive && x.State == EditSessionState.Active)
            .ToListAsync(ct);
        foreach (var expired in sessions.Where(x => x.ExpiresAt <= now)) expired.Expire(now);
        await db.SaveChangesAsync(ct);

        var active = sessions.FirstOrDefault(x => x.State == EditSessionState.Active);
        if (active is not null)
        {
            if (active.UserName == actor.UserName)
            {
                var latest = await db.ArtifactDraftSnapshots.AsNoTracking().Where(x => x.SessionId == active.Id)
                    .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
                return Results.Ok(MapSession(active, latest?.DraftJson ?? active.DraftJson, true, artifact.Adapter));
            }
            return Results.Conflict(new
            {
                error = $"{active.UserName} has this artifact checked out.", code = "exclusive_lock",
                holder = active.UserName, active.OpenedAt, lastActivityAt = active.UpdatedAt, active.ExpiresAt, readOnly = true
            });
        }

        var hash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(artifact.SnapshotJson));
        var session = new ArtifactEditSession(artifact.ProjectId, policy.CanonicalType, request.ArtifactId, artifact.RevisionId,
            hash, artifact.SnapshotJson, actor.UserName, now, true, leaseMinutes);
        db.ArtifactEditSessions.Add(session);
        db.ArtifactDraftSnapshots.Add(new ArtifactDraftSnapshot(artifact.ProjectId, session.Id, policy.CanonicalType,
            request.ArtifactId, 1, artifact.SnapshotJson, hash, actor.UserName, now));
        db.AuditEvents.Add(new AuditEvent(request.ArtifactId, "ArtifactCheckedOut", actor.UserName,
            $"Checked out {policy.CanonicalType} until {session.ExpiresAt:O} using adapter {artifact.Adapter}.", now));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "Another user obtained the edit lock first. Refresh to see the current holder.", code = "exclusive_lock" });
        }
        return Results.Created($"/api/controlled-editing/sessions/{session.Id}", MapSession(session, artifact.SnapshotJson, false, artifact.Adapter));
    }

    private static async Task<IResult> AutosaveAsync(Guid id, UniversalAutosaveRequest request, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(request.DraftJson) > 2_000_000)
            return Results.BadRequest(new { error = "The recoverable draft exceeds the 2 MB controlled autosave limit." });
        try
        {
            using var parsed = JsonDocument.Parse(request.DraftJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "The autosave payload must be a JSON object." });
        }
        catch (JsonException) { return Results.BadRequest(new { error = "The autosave payload is not valid JSON." }); }

        var session = await db.ArtifactEditSessions.SingleOrDefaultAsync(x => x.Id == id && x.IsExclusive, ct);
        if (session is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, session.ProjectId, ct) || session.UserName != http.UserAccount().UserName)
            return Results.Forbid();
        if (!ControlledArtifactEditPolicies.TryResolve(session.ArtifactType, out var policy))
            return Results.Conflict(new { error = "The session artifact policy is no longer available.", code = "policy_missing" });

        try
        {
            var now = DateTimeOffset.UtcNow;
            session.Save(request.DraftJson, request.ExpectedVersion, now, policy.NormalizeLease(request.LeaseMinutes));
            var hash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(request.DraftJson));
            db.ArtifactDraftSnapshots.Add(new ArtifactDraftSnapshot(session.ProjectId, session.Id, session.ArtifactType,
                session.ArtifactId, session.Version, request.DraftJson, hash, http.UserAccount().UserName, now));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { session.Id, session.Version, session.UpdatedAt, session.ExpiresAt, status = "Saved", hash });
        }
        catch (DomainException ex) { return Results.Conflict(new { error = ex.Message, code = "edit_session_conflict" }); }
    }

    private static async Task<IResult> HeartbeatAsync(Guid id, UniversalHeartbeatRequest request, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        var session = await db.ArtifactEditSessions.SingleOrDefaultAsync(x => x.Id == id && x.IsExclusive, ct);
        if (session is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, session.ProjectId, ct) || session.UserName != http.UserAccount().UserName)
            return Results.Forbid();
        if (!ControlledArtifactEditPolicies.TryResolve(session.ArtifactType, out var policy))
            return Results.Conflict(new { error = "The session artifact policy is no longer available.", code = "policy_missing" });
        try
        {
            session.Heartbeat(request.ExpectedVersion, DateTimeOffset.UtcNow, policy.NormalizeLease(request.LeaseMinutes));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { session.Id, session.Version, session.UpdatedAt, session.ExpiresAt });
        }
        catch (DomainException ex) { return Results.Conflict(new { error = ex.Message, code = "edit_session_conflict" }); }
    }

    private static async Task<IResult> CheckInAsync(Guid id, UniversalCheckInRequest request, HttpContext http,
        ControlledEditingCheckInEngine engine, CancellationToken ct)
    {
        var result = await engine.CheckInAsync(id, request.ExpectedVersion, http.UserAccount(),
            DateTimeOffset.UtcNow, ct);
        if (result.Success)
            return Results.Ok(new
            {
                success = true,
                resultingArtifactVersion = result.ResultingArtifactVersion,
                resultingHash = result.ResultingHash,
                sessionClosed = true,
                leaseReleased = true,
                revision = result.Revision,
                evidenceId = result.EvidenceId
            });
        var error = new { error = result.Error, code = result.Code, evidenceId = result.EvidenceId };
        return result.Status switch
        {
            ControlledCheckInStatus.NotFound => Results.NotFound(error),
            ControlledCheckInStatus.Forbidden => Results.Json(error, statusCode: StatusCodes.Status403Forbidden),
            ControlledCheckInStatus.InvalidDraft => Results.BadRequest(error),
            _ => Results.Conflict(error)
        };
    }

    private static async Task<IResult> DiscardAsync(Guid id, UniversalCloseSessionRequest request, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        var session = await db.ArtifactEditSessions.SingleOrDefaultAsync(x => x.Id == id && x.IsExclusive, ct);
        if (session is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, session.ProjectId, ct) || session.UserName != http.UserAccount().UserName)
            return Results.Forbid();
        try
        {
            var now = DateTimeOffset.UtcNow;
            session.Close(EditSessionState.Abandoned, request.ExpectedVersion, now, http.UserAccount().UserName,
                string.IsNullOrWhiteSpace(request.Reason) ? "Controlled draft checkout discarded." : request.Reason);
            db.AuditEvents.Add(new AuditEvent(session.ArtifactId, "EditSessionDiscarded", http.UserAccount().UserName,
                session.ClosedReason ?? "Controlled draft checkout discarded.", now));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }
        catch (DomainException ex) { return Results.Conflict(new { error = ex.Message, code = "edit_session_conflict" }); }
    }

    private static async Task<IResult> ForceUnlockAsync(Guid id, UniversalForceUnlockRequest request, HttpContext http,
        AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        var session = await db.ArtifactEditSessions.SingleOrDefaultAsync(x => x.Id == id && x.IsExclusive, ct);
        if (session is null) return Results.NotFound();
        var actor = http.UserAccount();
        if (!await http.HasProjectAccessAsync(db, session.ProjectId, ct)) return Results.Forbid();
        if (!actor.IsAdministrator && !await http.HasProjectRoleAsync(db, identity, session.ProjectId, ct, ProgramRole.ConfigurationManager))
            return Results.Forbid();
        try
        {
            var now = DateTimeOffset.UtcNow;
            session.ForceUnlock(actor.UserName, request.Reason, now);
            db.AuditEvents.Add(new AuditEvent(session.ArtifactId, "EditSessionForceUnlocked", actor.UserName,
                $"Force-unlocked {session.ArtifactType} held by {session.UserName}. Reason: {request.Reason}", now));
            db.SecurityAuditEvents.Add(new SecurityAuditEvent("ForcedUnlock", actor.UserName,
                $"{session.ArtifactType}:{session.ArtifactId}", "Success", request.Reason,
                http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static object MapSession(ArtifactEditSession session, string draftJson, bool resumed, string adapter) => new
    {
        session.Id, session.ArtifactType, session.ArtifactId, session.Version, session.UserName,
        session.OpenedAt, lastActivityAt = session.UpdatedAt, session.ExpiresAt, session.BaseSnapshotHash,
        draftJson, resumed, readOnly = false, status = "Saved", adapter
    };

    private static async Task<ResolvedControlledArtifact?> ResolveAsync(ControlledArtifactEditPolicy policy, Guid artifactId,
        AeroLinkDbContext db, CancellationToken ct)
    {
        switch (policy.Family)
        {
            case ControlledArtifactFamily.ChangeRequest:
            {
                var item = await db.SystemChangeRequests.AsNoTracking().Include(x => x.RequirementChanges)
                    .SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                return item is null ? null : new(item.ProjectId, item.State.ToString(), null,
                    SystemChangeRequestControlledEditingAdapter.Snapshot(item),
                    "ChangeRequest");
            }
            case ControlledArtifactFamily.RequirementProposal:
            {
                var item = await db.RequirementChanges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (item is null) return null;
                var parent = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == item.ScrId, ct);
                return new(parent.ProjectId, parent.State.ToString(), null,
                    JsonSerializer.Serialize(new { item.Id, item.BaseNumber, item.Revision, level = item.Level.ToString(), kind = item.Kind.ToString(), item.Statement, item.Rationale, item.VerificationMethod, item.RichText, item.AttributesJson, item.ImpactDispositionJson, parentVersion = parent.Version }),
                    "RequirementProposal");
            }
            case ControlledArtifactFamily.SpecificationStructure:
            {
                var specification = await db.RequirementSpecifications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (specification is not null)
                {
                    var nodes = await db.SpecificationNodes.AsNoTracking().Where(x => x.SpecificationId == artifactId)
                        .OrderBy(x => x.Position).Select(x => new { x.Id, x.ParentId, x.Position, type = x.Type.ToString(), x.Heading, x.RequirementArtifactId }).ToListAsync(ct);
                    return new(specification.ProjectId, "InWork", null,
                        JsonSerializer.Serialize(new { specification.Id, specification.DocumentNumber, specification.Title, specification.Level, specification.Description, nodes }),
                        "RequirementSpecification");
                }
                var node = await db.SpecificationNodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (node is null) return null;
                var owner = await db.RequirementSpecifications.AsNoTracking().SingleAsync(x => x.Id == node.SpecificationId, ct);
                return new(owner.ProjectId, "InWork", null,
                    JsonSerializer.Serialize(new { node.Id, node.SpecificationId, node.ParentId, node.Position, type = node.Type.ToString(), node.Heading, node.RequirementArtifactId }),
                    "SpecificationNode");
            }
            case ControlledArtifactFamily.TestProcedure:
            {
                var revision = await db.TestProcedureRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (revision is not null)
                {
                    var procedure = await db.TestProcedures.AsNoTracking().SingleAsync(x => x.Id == revision.ProcedureId, ct);
                    return new(procedure.ProjectId, revision.State.ToString(), revision.Id,
                        JsonSerializer.Serialize(new { procedureId = procedure.Id, procedure.BaseNumber, procedure.Title, procedure.OwnerId, level = procedure.Level.ToString(), revisionId = revision.Id, revision.Revision, revision.Objective, revision.Preconditions, revision.Steps, revision.ExpectedResult, state = revision.State.ToString(), revision.AuthorId }),
                        "TestProcedureRevision");
                }
                var procedureOnly = await db.TestProcedures.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (procedureOnly is null) return null;
                var latest = await db.TestProcedureRevisions.AsNoTracking().Where(x => x.ProcedureId == artifactId)
                    .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
                return new(procedureOnly.ProjectId, latest?.State.ToString() ?? "Draft", latest?.Id,
                    JsonSerializer.Serialize(new { procedureOnly.Id, procedureOnly.BaseNumber, procedureOnly.Title, procedureOnly.OwnerId, level = procedureOnly.Level.ToString(), latest }),
                    "TestProcedure");
            }
            case ControlledArtifactFamily.TraceLinkProposal:
            {
                var item = await db.RequirementTraces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                return item is null ? null : new(item.ProjectId, "Proposed", null, JsonSerializer.Serialize(item), "RequirementTraceLink");
            }
            case ControlledArtifactFamily.ReleasePlanning:
            {
                var item = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                return item is null ? null : new(item.ProjectId, item.State.ToString(), null,
                    JsonSerializer.Serialize(new { item.Id, item.BaseNumber, item.Revision, item.Name, item.ReleaseId, item.PredecessorBaselineId, state = item.State.ToString(), item.ContentHash, item.RequirementsHash }),
                    "CandidateBaseline");
            }
            default:
                return null;
        }
    }

    private sealed record ResolvedControlledArtifact(Guid ProjectId, string State, Guid? RevisionId, string SnapshotJson, string Adapter);
}

public sealed record UniversalCheckoutRequest(string ArtifactType, Guid ArtifactId, int? LeaseMinutes = null);
public sealed record UniversalAutosaveRequest(long ExpectedVersion, string DraftJson, int? LeaseMinutes = null);
public sealed record UniversalHeartbeatRequest(long ExpectedVersion, int? LeaseMinutes = null);
public sealed record UniversalCheckInRequest(long ExpectedVersion);
public sealed record UniversalCloseSessionRequest(long ExpectedVersion, string? Reason = null);
public sealed record UniversalForceUnlockRequest(string Reason);
