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
        group.MapPost("/artifacts", CreateArtifactAsync);
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

    private static async Task<IResult> CreateArtifactAsync(CreateControlledArtifactRequest request, HttpContext http,
        AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        if (!ControlledArtifactEditPolicies.TryResolve(request.ArtifactType, out var policy) || policy.Family is not (
                ControlledArtifactFamily.DocumentTemplate or ControlledArtifactFamily.ProblemReport or ControlledArtifactFamily.ConfigurationChangeSet))
            return Results.BadRequest(new { error = "This endpoint creates DocumentTemplate, ProblemReport, and ConfigurationChangeSet artifacts only." });
        if (!await http.HasProjectAccessAsync(db, request.ProjectId, ct) || !await http.HasProjectRoleAsync(db, identity,
                request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
        var now = DateTimeOffset.UtcNow; var actor = http.UserAccount().UserName;
        try
        {
            object item = policy.Family switch
            {
                ControlledArtifactFamily.DocumentTemplate => new DocumentTemplate(request.ProjectId, request.Number, request.Title, request.Content ?? "", actor, now),
                ControlledArtifactFamily.ProblemReport => new ProblemReport(request.ProjectId, request.Number, request.Title, request.Content ?? "", request.Analysis ?? "", actor, now),
                _ => new ConfigurationChangeSet(request.ProjectId, request.Number, request.Title, request.Content ?? "", actor, now)
            };
            db.Add(item); await db.SaveChangesAsync(ct);
            var id = item switch { DocumentTemplate x => x.Id, ProblemReport x => x.Id, ConfigurationChangeSet x => x.Id, _ => Guid.Empty };
            return Results.Created($"/api/controlled-editing/artifacts/{id}", new { id, artifactType = policy.CanonicalType, state = "Draft" });
        }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "A controlled artifact with that identifier already exists in this project." }); }
    }

    private static async Task<IResult> GetStatusAsync(string artifactType, Guid artifactId, HttpContext http,
        AeroLinkDbContext db, CancellationToken ct)
    {
        if (!ControlledArtifactEditPolicies.TryResolve(artifactType, out var policy))
            return Results.BadRequest(new { error = $"'{artifactType}' is not a supported controlled draft artifact type.", code = "unsupported_artifact_type" });

        var artifact = await ResolveAsync(policy, artifactId, db, ct);
        if (artifact is null) return Results.NotFound();
        if (!await http.HasProjectAccessAsync(db, artifact.ProjectId, ct)) return Results.Forbid();
        var actor = http.UserAccount();

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
            editable = policy.IsEditableState(artifact.State) &&
                (artifact.GoverningAuthorId is null || actor.IsAdministrator ||
                 string.Equals(artifact.GoverningAuthorId, actor.UserName, StringComparison.OrdinalIgnoreCase)),
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
        var actor = http.UserAccount();
        if (artifact.GoverningAuthorId is not null && !actor.IsAdministrator &&
            !string.Equals(artifact.GoverningAuthorId, actor.UserName, StringComparison.OrdinalIgnoreCase))
            return Results.Forbid();
        if (!await http.HasProjectRoleAsync(db, identity, artifact.ProjectId, ct,
                ProgramRole.Engineer, ProgramRole.TestEngineer, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager))
            return Results.Forbid();
        if (!policy.IsEditableState(artifact.State))
            return Results.Conflict(new { error = $"{policy.CanonicalType} is in {artifact.State} and cannot be checked out.", code = "artifact_not_editable" });

        int leaseMinutes;
        try { leaseMinutes = policy.NormalizeLease(request.LeaseMinutes); }
        catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message, code = "invalid_lease" }); }

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
        if (artifact.AuditAggregateId is Guid auditAggregateId)
            // The narrative says what happened; the identifiers, the exact lease instant and the adapter are
            // evidence, and were previously spelled out in the sentence a reader sees.
            db.AuditEvents.Add(new AuditEvent(auditAggregateId, "ArtifactCheckedOut", actor.UserName,
                "Took exclusive control of the record for editing.", now,
                JsonSerializer.Serialize(new { canonicalType = policy.CanonicalType, artifactId = request.ArtifactId,
                    sessionId = session.Id, leaseExpiresAt = session.ExpiresAt, adapter = artifact.Adapter })));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Concurrent checkout requests can legitimately originate from the same browser. In particular,
            // React development StrictMode mounts a controlled editor twice. The exclusive database index is
            // still the source of truth; once it selects a winner, treat that winner as a resumable checkout
            // when it belongs to this actor instead of presenting a spurious foreign-lock error.
            db.ChangeTracker.Clear();
            // SQLite cannot compare a DateTimeOffset server-side, so the lease expiry is applied in memory.
            // Translating it in the query turned this recovery path — the one that exists to turn a
            // collision into a usable answer — into a 500 on every SQLite deployment.
            var winner = (await db.ArtifactEditSessions
                    .Where(x => x.ArtifactId == request.ArtifactId && x.ArtifactType == policy.CanonicalType
                        && x.IsExclusive && x.State == EditSessionState.Active).ToListAsync(ct))
                .SingleOrDefault(x => x.ExpiresAt > DateTimeOffset.UtcNow);
            if (winner?.UserName == actor.UserName)
            {
                var latest = await db.ArtifactDraftSnapshots.AsNoTracking().Where(x => x.SessionId == winner.Id)
                    .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
                return Results.Ok(MapSession(winner, latest?.DraftJson ?? winner.DraftJson, true, artifact.Adapter));
            }
            if (winner is not null)
                return Results.Conflict(new
                {
                    error = $"{winner.UserName} has this artifact checked out.", code = "exclusive_lock",
                    holder = winner.UserName, winner.OpenedAt, lastActivityAt = winner.UpdatedAt, winner.ExpiresAt, readOnly = true
                });
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
                if (item is null) return null;
                var reportIds = await db.ProblemReportLinks.AsNoTracking().Where(link =>
                        link.ArtifactType == "ChangeRequest" && link.ArtifactId == item.Id
                        && link.Relationship == "ProposedCorrectiveAction")
                    .Select(link => link.ProblemReportId).OrderBy(id => id).ToListAsync(ct);
                return new(item.ProjectId, item.State.ToString(), null,
                    SystemChangeRequestControlledEditingAdapter.Snapshot(item, reportIds),
                    "ChangeRequest", item.Id, item.AuthorId);
            }
            case ControlledArtifactFamily.RequirementProposal:
            {
                var item = await db.RequirementChanges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (item is null) return null;
                var parent = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == item.ScrId, ct);
                return new(parent.ProjectId, parent.State.ToString(), null,
                    RequirementProposalControlledEditingAdapter.Snapshot(item, parent.Version),
                    "RequirementProposal", parent.Id, parent.AuthorId);
            }
            case ControlledArtifactFamily.SpecificationStructure:
            {
                var specification = await db.RequirementSpecifications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (specification is not null)
                {
                    var nodes = await db.SpecificationNodes.AsNoTracking().Where(x => x.SpecificationId == artifactId).ToListAsync(ct);
                    return new(specification.ProjectId, "InWork", null,
                        SpecificationStructureControlledEditingAdapter.Snapshot(specification, nodes),
                        "RequirementSpecification");
                }
                var node = await db.SpecificationNodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (node is null) return null;
                var owner = await db.RequirementSpecifications.AsNoTracking().SingleAsync(x => x.Id == node.SpecificationId, ct);
                var ownerNodes = await db.SpecificationNodes.AsNoTracking().Where(x => x.SpecificationId == owner.Id).ToListAsync(ct);
                return new(owner.ProjectId, "InWork", null,
                    SpecificationStructureControlledEditingAdapter.Snapshot(owner, ownerNodes), "RequirementSpecification");
            }
            case ControlledArtifactFamily.TestProcedure:
            {
                var revision = await db.TestProcedureRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (revision is not null)
                {
                    var procedure = await db.TestProcedures.AsNoTracking().SingleAsync(x => x.Id == revision.ProcedureId, ct);
                    return new(procedure.ProjectId, revision.State.ToString(), revision.Id,
                        TestProcedureControlledEditingAdapter.Snapshot(procedure, revision), "TestProcedureRevision");
                }
                var procedureOnly = await db.TestProcedures.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (procedureOnly is null) return null;
                var latest = await db.TestProcedureRevisions.AsNoTracking().Where(x => x.ProcedureId == artifactId)
                    .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
                return latest is null ? null : new(procedureOnly.ProjectId, latest.State.ToString(), latest.Id,
                    TestProcedureControlledEditingAdapter.Snapshot(procedureOnly, latest), "TestProcedureRevision");
            }
            case ControlledArtifactFamily.TraceLinkProposal:
            {
                var item = await db.RequirementTraces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                return item is null ? null : new(item.ProjectId, "Proposed", null,
                    TraceLinkProposalControlledEditingAdapter.Snapshot(item), "RequirementTraceLink");
            }
            case ControlledArtifactFamily.ReleasePlanning:
            {
                var item = await db.CandidateBaselines.SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                if (item is null) return null;
                await db.Entry(item).Collection(x => x.Selections).LoadAsync(ct);
                return new(item.ProjectId, item.State.ToString(), null,
                    ReleasePlanningControlledEditingAdapter.Snapshot(item), "CandidateBaseline");
            }
            case ControlledArtifactFamily.DocumentTemplate:
            {
                var item = await db.DocumentTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                return item is null ? null : new(item.ProjectId, item.State.ToString(), null,
                    DocumentTemplateControlledEditingAdapter.Snapshot(item), "DocumentTemplate");
            }
            case ControlledArtifactFamily.ProblemReport:
            {
                var item = await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                // The responsible engineer governs the record, so a checkout is refused up front to anybody
                // whose check-in the aggregate would refuse anyway. Offering a lease that cannot be
                // completed is worse than refusing it.
                //
                // No audit aggregate: AuditEvent.AggregateId is a foreign key to a change request, and a
                // Problem Report's controlled history is its own ProblemReportRevision chain, which the
                // adapter writes on check-in.
                return item is null ? null : new(item.ProjectId, item.State.ToString(), null,
                    ProblemReportControlledEditingAdapter.Snapshot(item), "ProblemReport",
                    GoverningAuthorId: item.ResponsibleEngineerId);
            }
            case ControlledArtifactFamily.ConfigurationChangeSet:
            {
                var item = await db.ConfigurationChangeSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId, ct);
                return item is null ? null : new(item.ProjectId, item.State.ToString(), null,
                    ConfigurationChangeSetControlledEditingAdapter.Snapshot(item), "ConfigurationChangeSet");
            }
            default:
                return null;
        }
    }

    private sealed record ResolvedControlledArtifact(Guid ProjectId, string State, Guid? RevisionId,
        string SnapshotJson, string Adapter, Guid? AuditAggregateId = null, string? GoverningAuthorId = null);
}

public sealed record UniversalCheckoutRequest(string ArtifactType, Guid ArtifactId, int? LeaseMinutes = null);
public sealed record CreateControlledArtifactRequest(string ArtifactType, Guid ProjectId, string Number, string Title, string? Content, string? Analysis);
public sealed record UniversalAutosaveRequest(long ExpectedVersion, string DraftJson, int? LeaseMinutes = null);
public sealed record UniversalHeartbeatRequest(long ExpectedVersion, int? LeaseMinutes = null);
public sealed record UniversalCheckInRequest(long ExpectedVersion);
public sealed record UniversalCloseSessionRequest(long ExpectedVersion, string? Reason = null);
public sealed record UniversalForceUnlockRequest(string Reason);
