using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public sealed record AssignVerificationImpactRequest(string EngineerId);
public sealed record ResolveVerificationImpactRequest(VerificationImpactOutcome Outcome, string Rationale, Guid? ProcedureId);
public sealed record ReopenVerificationImpactRequest(string Rationale);

public static class VerificationImpactEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkVerificationImpactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/releases/{releaseId:guid}/verification-impact", async (Guid releaseId, bool? outstandingOnly,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, CancellationToken ct) =>
        {
            var projectId = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId == Guid.Empty) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var items = outstandingOnly == true
                ? await service.OutstandingForReleaseAsync(releaseId, ct)
                : await service.ForReleaseAsync(releaseId, ct);
            return Results.Ok(await MapAsync(items, db, ct));
        });

        app.MapPost("/api/verification-impact/{id:guid}/assign", async (Guid id, AssignVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct, ProgramRole.TestLead))
                return Results.Forbid();
            try
            {
                item.AssignToEngineer(http.UserAccount().UserName, request.EngineerId, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/verification-impact/{id:guid}/resolve", async (Guid id, ResolveVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();

            ApprovedProcedureSelection? selectedProcedure = null;
            if (request.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed && request.ProcedureId is not null)
                selectedProcedure = await service.FindApprovedProcedureAsync(item.ProjectId, request.ProcedureId.Value, ct);
            if (request.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed && selectedProcedure is null)
                return Results.BadRequest(new
                {
                    error = "Coverage can only be confirmed against an approved procedure in this Project."
                });

            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                item.Resolve(actor, request.Outcome, request.Rationale, now,
                    selectedProcedure?.ProcedureId, selectedProcedure?.RevisionId);
                db.VerificationImpactDecisionHistory.Add(new VerificationImpactDecisionHistory(
                    item.Id, VerificationImpactHistoryAction.Resolved, item.Outcome,
                    item.ResolvedProcedureId, item.ResolvedProcedureRevisionId,
                    item.ResolutionRationale, actor, now));
                await service.ApplyResolvedCoverageAsync(item, now, ct);
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/verification-impact/{id:guid}/reopen", async (Guid id, ReopenVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            try
            {
                var actor = http.UserAccount().UserName;
                var now = DateTimeOffset.UtcNow;
                db.VerificationImpactDecisionHistory.Add(new VerificationImpactDecisionHistory(
                    item.Id, VerificationImpactHistoryAction.Reopened, item.Outcome,
                    item.ResolvedProcedureId, item.ResolvedProcedureRevisionId,
                    request.Rationale, actor, now));
                await service.ReopenResolvedCoverageAsync(item, request.Rationale, now, ct);
                item.Reopen(actor, request.Rationale, now);
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }

    private static async Task<IReadOnlyList<object>> MapAsync(
        IReadOnlyCollection<VerificationImpactItem> items, AeroLinkDbContext db, CancellationToken ct)
    {
        var exactIds = items.Where(x => x.ResolvedProcedureRevisionId is not null)
            .Select(x => x.ResolvedProcedureRevisionId!.Value).Distinct().ToList();
        var exactRows = exactIds.Count == 0
            ? []
            : await LoadProcedureRowsAsync(db, exactIds, [], ct);
        var exact = exactRows.ToDictionary(x => x.RevisionId, ToSelection);

        var legacyIds = items.Where(x => x.ResolvedProcedureRevisionId is null && x.ResolvedProcedureId is not null)
            .Select(x => x.ResolvedProcedureId!.Value).Distinct().ToList();
        var legacyRows = legacyIds.Count == 0
            ? []
            : (await LoadProcedureRowsAsync(db, [], legacyIds, ct))
                .Where(x => x.State == TestProcedureState.Approved).ToList();
        var legacy = legacyRows.GroupBy(x => x.ProcedureId).ToDictionary(x => x.Key,
            x => ToSelection(x.OrderByDescending(y => y.Revision).First()));

        var itemIds = items.Select(x => x.Id).ToList();
        var historyRows = await db.VerificationImpactDecisionHistory.AsNoTracking()
            .Where(x => itemIds.Contains(x.VerificationImpactItemId))
            .ToListAsync(ct);
        var history = historyRows.OrderBy(x => x.OccurredAt).ToList();

        return items.Select(x =>
        {
            ApprovedProcedureSelection? procedure = null;
            if (x.ResolvedProcedureRevisionId is not null)
                exact.TryGetValue(x.ResolvedProcedureRevisionId.Value, out procedure);
            else if (x.ResolvedProcedureId is not null)
                legacy.TryGetValue(x.ResolvedProcedureId.Value, out procedure);
            return (object)new
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
                x.ResolvedProcedureRevisionId,
                resolvedProcedure = procedure is null ? null : new
                {
                    id = procedure.ProcedureId,
                    revisionId = procedure.RevisionId,
                    procedure.DisplayNumber,
                    procedure.Title,
                    procedure.Level,
                    procedure.State,
                    configuration = new { x.RequirementRevisionId, procedureRevisionId = procedure.RevisionId }
                },
                x.ResolutionRationale,
                x.ResolvedBy,
                x.ResolvedAt,
                x.RaisedAt,
                x.BlocksBaselineApproval,
                decisionHistory = history.Where(h => h.VerificationImpactItemId == x.Id).Select(h => new
                {
                    h.Id,
                    action = h.Action.ToString(),
                    outcome = h.Outcome?.ToString(),
                    h.ProcedureId,
                    h.ProcedureRevisionId,
                    h.Rationale,
                    actor = h.ActorId,
                    h.OccurredAt
                })
            };
        }).ToList();
    }

    private static async Task<List<ProcedureRow>> LoadProcedureRowsAsync(
        AeroLinkDbContext db, IReadOnlyCollection<Guid> revisionIds,
        IReadOnlyCollection<Guid> procedureIds, CancellationToken ct)
    {
        var rows = await (
            from revision in db.TestProcedureRevisions.AsNoTracking()
            join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
            where revisionIds.Contains(revision.Id) || procedureIds.Contains(procedure.Id)
            select new
            {
                ProcedureId = procedure.Id,
                RevisionId = revision.Id,
                revision.Revision,
                procedure.BaseNumber,
                procedure.Title,
                procedure.Level,
                revision.State
            }).ToListAsync(ct);
        return rows.Select(x => new ProcedureRow(x.ProcedureId, x.RevisionId, x.Revision, x.BaseNumber,
            x.Title, x.Level.ToString(), x.State)).ToList();
    }

    private static ApprovedProcedureSelection ToSelection(ProcedureRow row) =>
        new(row.ProcedureId, row.RevisionId, row.Revision, $"{row.BaseNumber}.{row.Revision:D2}",
            row.Title, row.Level, row.State.ToString());

    private sealed record ProcedureRow(Guid ProcedureId, Guid RevisionId, int Revision, string BaseNumber,
        string Title, string Level, TestProcedureState State);
}
