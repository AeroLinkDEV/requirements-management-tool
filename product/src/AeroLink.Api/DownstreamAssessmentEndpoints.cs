using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public sealed record DownstreamAssignmentRequest(string EngineerId);
public sealed record DownstreamNoChangeRequest(string Rationale);
public sealed record DownstreamLinkRequest(Guid ChangeRequestId);
public sealed record DownstreamSubmitRequest(string ApproverId);
public sealed record DownstreamReturnRequest(string Rationale);

public static class DownstreamAssessmentEndpoints
{
    public static void MapDownstreamAssessmentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/downstream-assessments", async (Guid projectId, Guid releaseId,
            RequirementLevel? targetLevel, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var source = db.DownstreamChangeAssessments.AsNoTracking().Include(x => x.ChangeRequestLinks)
                .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId);
            if (targetLevel is not null) source = source.Where(x => x.TargetLevel == targetLevel);
            // SQLite cannot order DateTimeOffset server-side; materialize first so local/test and PostgreSQL
            // deployments return the same ordered queue instead of the local API failing with a 500.
            var rows = (await source.ToListAsync(ct))
                .OrderBy(x => x.State == DownstreamAssessmentState.Superseded)
                .ThenByDescending(x => x.UpdatedAt).ToList();
            var sourceIds = rows.Select(x => x.SourceChangeRequestId).ToList();
            var requests = await db.SystemChangeRequests.AsNoTracking().Include(x => x.RequirementChanges)
                .Where(x => sourceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            return Results.Ok(rows.Select(x =>
            {
                requests.TryGetValue(x.SourceChangeRequestId, out var request);
                return new
                {
                    x.Id, x.ProjectId, x.ReleaseId, x.SourceChangeRequestId, x.SourceChangeRequestNumber,
                    targetLevel = x.TargetLevel.ToString(), state = x.State.ToString(), outcome = x.Outcome.ToString(),
                    x.AssignedEngineerId, x.Rationale, x.SubmittedBy, x.SelectedApproverId, x.SubmittedAt,
                    x.ApprovedBy, x.ApprovedAt, x.SupersededByAssessmentId, x.SupersededReason,
                    x.CreatedAt, x.UpdatedAt, x.Version,
                    sourceTitle = request?.Title ?? "Approved upstream change",
                    sourceChanges = request?.RequirementChanges.Select(change => new
                    {
                        change.Id, change.DisplayNumber, level = change.Level.ToString(), kind = change.Kind.ToString(),
                        change.Statement
                    }) ?? [],
                    linkedChangeRequests = x.ChangeRequestLinks.OrderBy(link => link.ChangeRequestNumber).Select(link => new
                    {
                        link.ChangeRequestId, link.ChangeRequestNumber, link.LinkedBy, link.LinkedAt
                    })
                };
            }));
        });

        app.MapPost("/api/downstream-assessments/{id:guid}/assign", async (Guid id,
            DownstreamAssignmentRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var assessment = await db.DownstreamChangeAssessments.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (assessment is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == assessment.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build downstream assessments are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, assessment.ProjectId, ct,
                    ProgramRole.Engineer, ProgramRole.ProgramManager)) return Results.Forbid();
            try
            {
                assessment.Assign(http.UserAccount().UserName, request.EngineerId, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct); return Results.Ok(new { assessment.Id, assessment.AssignedEngineerId, assessment.Version });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/downstream-assessments/{id:guid}/no-change", async (Guid id,
            DownstreamNoChangeRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var assessment = await db.DownstreamChangeAssessments.Include(x => x.ChangeRequestLinks)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (assessment is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == assessment.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build downstream assessments are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, assessment.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ProgramManager))
                return Results.Forbid();
            try
            {
                assessment.RecordNoChange(http.UserAccount().UserName, request.Rationale, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct); return Results.Ok(new { assessment.Id, outcome = assessment.Outcome.ToString(), assessment.Version });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/downstream-assessments/{id:guid}/change-requests", async (Guid id,
            DownstreamLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var assessment = await db.DownstreamChangeAssessments.Include(x => x.ChangeRequestLinks)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (assessment is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == assessment.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build downstream assessments are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, assessment.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ProgramManager))
                return Results.Forbid();
            var changeRequest = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
                .SingleOrDefaultAsync(x => x.Id == request.ChangeRequestId, ct);
            if (changeRequest is null) return Results.NotFound();
            if (changeRequest.ProjectId != assessment.ProjectId || changeRequest.TargetReleaseId != assessment.ReleaseId
                || changeRequest.Type != ChangeRequestType.Software)
                return Results.BadRequest(new { error = "Choose a Software SWCR from this Project and build." });
            if (changeRequest.State != ScrState.Draft)
                return Results.BadRequest(new { error = "Only a Draft SWCR can accept another upstream assessment." });
            if (changeRequest.RequirementChanges.Count != 0 && changeRequest.RequirementChanges.All(x => x.Level != assessment.TargetLevel))
                return Results.BadRequest(new { error = $"That SWCR does not contain {assessment.TargetLevel} requirement work." });
            try
            {
                assessment.LinkChangeRequest(http.UserAccount().UserName, changeRequest.Id,
                    changeRequest.DisplayNumber, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct); return Results.Ok(new { assessment.Id, outcome = assessment.Outcome.ToString(), assessment.Version });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/downstream-assessments/{id:guid}/submit", async (Guid id,
            DownstreamSubmitRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var assessment = await db.DownstreamChangeAssessments.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (assessment is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == assessment.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build downstream assessments are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, assessment.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ProgramManager))
                return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.ApproverId))
                return Results.BadRequest(new { error = "Select an independent downstream assessment approver." });
            var approver = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.UserName == request.ApproverId.Trim().ToLowerInvariant() && x.State == AccountState.Active, ct);
            if (approver is null) return Results.BadRequest(new { error = "Select an active AeroLink approver." });
            var programId = await db.Projects.Where(x => x.Id == assessment.ProjectId).Select(x => x.ProgramId).SingleAsync(ct);
            if (!await identity.HasRoleAsync(approver.Id, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct))
                return Results.BadRequest(new { error = $"{approver.DisplayName} does not hold Approver authority for this Program." });
            try
            {
                assessment.Submit(http.UserAccount().UserName, approver.UserName, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct); return Results.Ok(new { assessment.Id, state = assessment.State.ToString(), assessment.Version });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/downstream-assessments/{id:guid}/approve", async (Guid id,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var assessment = await db.DownstreamChangeAssessments.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (assessment is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == assessment.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build downstream assessments are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, assessment.ProjectId, ct, ProgramRole.Approver))
                return Results.Forbid();
            try
            {
                assessment.Approve(http.UserAccount().UserName, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct); return Results.Ok(new { assessment.Id, state = assessment.State.ToString(), assessment.Version });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/downstream-assessments/{id:guid}/return", async (Guid id,
            DownstreamReturnRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var assessment = await db.DownstreamChangeAssessments.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (assessment is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == assessment.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build downstream assessments are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, assessment.ProjectId, ct, ProgramRole.Approver))
                return Results.Forbid();
            try
            {
                assessment.ReturnToWork(http.UserAccount().UserName, request.Rationale, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct); return Results.Ok(new { assessment.Id, state = assessment.State.ToString(), assessment.Version });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }
}
