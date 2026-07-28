using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public sealed record AssignVerificationImpactRequest(string EngineerId);
public sealed record ResolveVerificationImpactRequest(VerificationImpactOutcome Outcome, string Rationale, Guid? ProcedureId);

public static class VerificationImpactEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkVerificationImpactEndpoints(this IEndpointRouteBuilder app)
    {
        // The queue exists for a release, not for a baseline, so verification work is visible on an in-work
        // release long before there is a configuration to compute coverage against.
        app.MapGet("/api/releases/{releaseId:guid}/verification-impact", async (Guid releaseId, bool? outstandingOnly,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, CancellationToken ct) =>
        {
            var projectId = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId).Select(x => x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId == Guid.Empty) return Results.NotFound();
            // Any authorised member of the Program may see what verification the release owes.
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var items = outstandingOnly == true
                ? await service.OutstandingForReleaseAsync(releaseId, ct)
                : await service.ForReleaseAsync(releaseId, ct);
            return Results.Ok(items.Select(Map));
        });

        app.MapPost("/api/verification-impact/{id:guid}/assign", async (Guid id, AssignVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            // Distribution is the test lead's authority.
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct, ProgramRole.TestLead)) return Results.Forbid();
            try
            {
                item.AssignToEngineer(http.UserAccount().UserName, request.EngineerId, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(Map(item));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/verification-impact/{id:guid}/resolve", async (Guid id, ResolveVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            // Deciding what a change means for verification belongs to the verification team, never to the
            // requirement author who declared the method.
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            if (request.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed
                && (request.ProcedureId is null || !await service.HasApprovedProcedureAsync(item.ProjectId, request.ProcedureId.Value, ct)))
                return Results.BadRequest(new { error = "Coverage can only be confirmed against an approved procedure in this Project." });
            try
            {
                var now = DateTimeOffset.UtcNow;
                item.Resolve(http.UserAccount().UserName, request.Outcome, request.Rationale, now, request.ProcedureId);
                await service.ApplyResolvedCoverageAsync(item, now, ct);
                await db.SaveChangesAsync(ct);
                return Results.Ok(Map(item));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }

    private static object Map(VerificationImpactItem x) => new
    {
        x.Id,
        x.ReleaseId,
        x.ChangeRequestId,
        trigger = x.Trigger.ToString(),
        state = x.State.ToString(),
        x.SubjectDisplayNumber,
        x.DeclaredVerificationMethod,
        x.RequirementChangeId,
        x.RequirementRevisionId,
        x.ProcedureId,
        x.AssignedEngineerId,
        x.AssignedByLeadId,
        x.AssignedAt,
        outcome = x.Outcome?.ToString(),
        x.ResolvedProcedureId,
        x.ResolutionRationale,
        x.ResolvedBy,
        x.ResolvedAt,
        x.RaisedAt,
        x.BlocksBaselineApproval
    };
}
