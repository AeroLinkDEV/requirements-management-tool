using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public sealed record IncludeInTestSetRequest(Guid[] ProcedureRevisionIds, TestSelectionReason Reason, string? Note = null);

/// <summary>
/// Choosing what a build has to run.
///
/// A build is rarely worth its whole test suite, and the decision about which procedures it needs is a
/// planning judgement made by a lead. These endpoints are that decision: read the three sets, put procedures
/// in, take them out. What has actually been run against them is recorded elsewhere and read back by the
/// release gates.
/// </summary>
public static class BuildTestSetEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkBuildTestSetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/releases/{releaseId:guid}/test-sets", async (Guid releaseId, HttpContext http,
            AeroLinkDbContext db, BuildTestSetService service, CancellationToken ct) =>
        {
            var projectId = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId == Guid.Empty) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();

            var sets = await service.EnsureForReleaseAsync(projectId, releaseId, ct);
            return Results.Ok(await DescribeAsync(sets, releaseId, db, ct));
        });

        app.MapPost("/api/releases/{releaseId:guid}/test-sets/{discipline}/procedures", async (Guid releaseId,
            TestChangeReviewDiscipline discipline, IncludeInTestSetRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, BuildTestSetService service, CancellationToken ct) =>
        {
            var release = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => new { x.ProjectId, x.IsReleased }).SingleOrDefaultAsync(ct);
            if (release is null) return Results.NotFound();
            if (release.IsReleased) return Results.Conflict(new { error = "A released build's test set is read-only." });
            if (!await CanPlanAsync(http, db, identity, release.ProjectId, ct)) return Results.Forbid();
            if (request.ProcedureRevisionIds.Length == 0)
                return Results.BadRequest(new { error = "Name at least one procedure to add." });

            // Every named revision has to be an approved procedure in this Project. Selecting a draft would
            // put the build behind a procedure that nobody has agreed says the right thing, and selecting one
            // from another Project would measure this release against work that has nothing to do with it.
            var reachable = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                   join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                                   where request.ProcedureRevisionIds.Contains(revision.Id)
                                         && procedure.ProjectId == release.ProjectId
                                         && revision.State == TestProcedureState.Approved
                                   select revision.Id).ToListAsync(ct);
            var unreachable = request.ProcedureRevisionIds.Except(reachable).ToList();
            if (unreachable.Count != 0)
                return Results.BadRequest(new
                {
                    error = "A build can only be set to run approved procedures from its own Project.",
                    code = "procedure_not_selectable"
                });

            var sets = await service.EnsureForReleaseAsync(release.ProjectId, releaseId, ct);
            var set = sets.SingleOrDefault(x => x.Discipline == discipline);
            if (set is null) return Results.NotFound(new { error = "That discipline has no test set on this build." });
            try
            {
                var actor = http.UserAccount().UserName;
                var now = DateTimeOffset.UtcNow;
                var added = reachable.Count(x => set.Include(actor, x, request.Reason, request.Note ?? "", now));
                await db.SaveChangesAsync(ct);
                // Says how many were new rather than how many were named. Selecting from two directions at
                // once is expected, so "8 named, 3 added" is the honest answer and the useful one.
                return Results.Ok(new { added, named = request.ProcedureRevisionIds.Length, set = (await DescribeAsync([set], releaseId, db, ct)).Single() });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/releases/{releaseId:guid}/test-sets/{discipline}/procedures/{procedureRevisionId:guid}",
            async (Guid releaseId, TestChangeReviewDiscipline discipline, Guid procedureRevisionId, HttpContext http,
                AeroLinkDbContext db, IdentityService identity, BuildTestSetService service, CancellationToken ct) =>
        {
            var release = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => new { x.ProjectId, x.IsReleased }).SingleOrDefaultAsync(ct);
            if (release is null) return Results.NotFound();
            if (release.IsReleased) return Results.Conflict(new { error = "A released build's test set is read-only." });
            if (!await CanPlanAsync(http, db, identity, release.ProjectId, ct)) return Results.Forbid();

            var sets = await service.EnsureForReleaseAsync(release.ProjectId, releaseId, ct);
            var set = sets.SingleOrDefault(x => x.Discipline == discipline);
            if (set is null) return Results.NotFound(new { error = "That discipline has no test set on this build." });
            // A procedure that is not in the set is already in the state the caller asked for. Two people
            // tidying the same set should not produce an error for whichever of them is slower.
            try
            {
                set.Exclude(procedureRevisionId, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok((await DescribeAsync([set], releaseId, db, ct)).Single());
            }
            catch (DomainException ex)
            {
                return Results.Conflict(new { error = ex.Message, code = "mandatory_changed_requirement_test" });
            }
        });

        return app;
    }

    /// <summary>
    /// Who decides what a build runs: the test lead who owns verification for it, and the Program manager
    /// accountable for the release. A test engineer records determinations against the set rather than
    /// choosing it, because scope and execution are different jobs even when one person does both.
    /// </summary>
    private static Task<bool> CanPlanAsync(HttpContext http, AeroLinkDbContext db, IdentityService identity,
        Guid projectId, CancellationToken ct) =>
        http.HasProjectRoleAsync(db, identity, projectId, ct, ProgramRole.TestLead, ProgramRole.ProgramManager);

    private static async Task<IReadOnlyList<object>> DescribeAsync(IReadOnlyCollection<BuildTestSet> sets,
        Guid releaseId, AeroLinkDbContext db, CancellationToken ct)
    {
        var revisionIds = sets.SelectMany(x => x.Entries).Select(x => x.ProcedureRevisionId).Distinct().ToList();
        var procedures = revisionIds.Count == 0
            ? []
            : await (from revision in db.TestProcedureRevisions.AsNoTracking()
                     join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                     where revisionIds.Contains(revision.Id)
                     select new { revision.Id, procedure.BaseNumber, revision.Revision, procedure.Title, procedure.Level })
                .ToListAsync(ct);
        var byRevision = procedures.ToDictionary(x => x.Id);

        // The latest run of each selected procedure on this build, so a reader sees exactly what the gate sees.
        // ExecutionScope is the shared authority: written separately, this list and the release gate drifted
        // into counting another release's determinations whenever the build had no immutable software build.
        var buildId = await db.SoftwareBuilds.AsNoTracking().Where(x => x.ReleaseId == releaseId)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        var latest = await ExecutionScope.LatestByProcedureAsync(db, revisionIds, releaseId, buildId, ct);
        var runIds = latest.Values.Select(x => x.Id).ToList();
        var evidenced = runIds.Count == 0
            ? []
            : (await db.TestExecutionEvidence.AsNoTracking().Where(x => runIds.Contains(x.TestExecutionId))
                .Select(x => x.TestExecutionId).Distinct().ToListAsync(ct)).ToHashSet();

        return sets.OrderBy(x => x.Discipline).Select(set => (object)new
        {
            set.Id,
            discipline = set.Discipline.ToString(),
            set.ReleaseId,
            set.Version,
            procedures = set.Entries.OrderBy(x => byRevision.TryGetValue(x.ProcedureRevisionId, out var p) ? p.BaseNumber : "")
                .Select(entry =>
                {
                    byRevision.TryGetValue(entry.ProcedureRevisionId, out var procedure);
                    latest.TryGetValue(entry.ProcedureRevisionId, out var run);
                    return new
                    {
                        entry.ProcedureRevisionId,
                        displayNumber = procedure is null ? "" : $"{procedure.BaseNumber}.{procedure.Revision:D2}",
                        title = procedure?.Title ?? "",
                        reason = entry.Reason.ToString(),
                        entry.Note,
                        entry.AddedBy,
                        entry.AddedAt,
                        latestOutcome = run?.Outcome.ToString(),
                        // The run itself, so evidence can be attached to it and a failure can be retested
                        // without the reader having to find the execution again somewhere else.
                        latestExecutionId = run?.Id,
                        latestExecutedAt = run?.ExecutedAt,
                        hasEvidence = run is not null && evidenced.Contains(run.Id),
                    };
                }).ToList(),
        }).ToList();
    }
}
