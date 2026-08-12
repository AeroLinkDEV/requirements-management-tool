using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// Administration of a team's review procedure.
///
/// Teams do not review the same way, and until now the only expression of that was the author picking names
/// by hand at submission — the procedure lived in people's heads, and nothing could tell whether a given
/// review had followed it. A recorded workflow makes the procedure a thing the product can check and a thing
/// an auditor can read.
///
/// Nothing here is required. A project with no workflow submits reviews exactly as before, with free
/// approver choice, because a rule nobody has written down must not become a rule that blocks work.
/// </summary>
public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        app.MapGet("/api/review-workflows", async (Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var rows = await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
                .Where(x => x.ProjectId == projectId).ToListAsync(ct);
            return Results.Ok(rows
                .OrderBy(x => x.AppliesTo).ThenBy(x => x.Name).ThenByDescending(x => x.Version)
                .Select(Map));
        });

        app.MapPost("/api/review-workflows", async (CreateReviewWorkflowRequest request, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            // Deciding how a team reviews is a configuration-management act, not an authoring one.
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                return Results.Forbid();
            try
            {
                var stages = request.Stages.Select(x => new ReviewWorkflowStageDraft(x.Name, x.RequiredRole)).ToList();
                var workflow = new ReviewWorkflow(request.ProjectId, request.Name, request.AppliesTo, request.Mode,
                    stages, http.UserAccount().UserName, DateTimeOffset.UtcNow);
                db.ReviewWorkflows.Add(workflow);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/review-workflows/{workflow.Id}", Map(workflow));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/review-workflows/{id:guid}/activate", async (Guid id, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var workflow = await db.ReviewWorkflows.Include(x => x.Stages).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (workflow is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, workflow.ProjectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                return Results.Forbid();
            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                // Two active procedures for the same kind of change request would mean the product silently
                // choosing which rules a review was judged by. Activating one retires the other.
                var superseded = await db.ReviewWorkflows
                    .Where(x => x.ProjectId == workflow.ProjectId && x.AppliesTo == workflow.AppliesTo
                                && x.State == ReviewWorkflowState.Active && x.Id != workflow.Id)
                    .ToListAsync(ct);
                foreach (var previous in superseded) previous.Retire(actor, now);
                workflow.Activate(actor, now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(Map(workflow));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/review-workflows/{id:guid}/revise", async (Guid id, ReviseReviewWorkflowRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var current = await db.ReviewWorkflows.Include(x => x.Stages).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (current is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, current.ProjectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                return Results.Forbid();
            try
            {
                // The prior version stays exactly as it was. A completed review has to remain explainable by
                // the procedure it was actually judged against.
                var stages = request.Stages.Select(x => new ReviewWorkflowStageDraft(x.Name, x.RequiredRole)).ToList();
                var next = current.Revise(request.Name, request.Mode, stages, http.UserAccount().UserName, DateTimeOffset.UtcNow);
                db.ReviewWorkflows.Add(next);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/review-workflows/{next.Id}", Map(next));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/review-workflows/{id:guid}/retire", async (Guid id, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var workflow = await db.ReviewWorkflows.Include(x => x.Stages).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (workflow is null) return Results.NotFound();
            if (!await http.HasProjectRoleAsync(db, identity, workflow.ProjectId, ct,
                    ProgramRole.ConfigurationManager, ProgramRole.ProgramManager, ProgramRole.Administrator))
                return Results.Forbid();
            try
            {
                workflow.Retire(http.UserAccount().UserName, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(Map(workflow));
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // What the author needs before choosing approvers: the stages they must fill, and who can fill each.
        //
        // The parameter keeps its name and widens its type. A caller asking for "System" or "Software" binds
        // exactly as before, because those values kept their names when the subject widened to cover test
        // change requests, so no existing client has to change to keep working.
        app.MapGet("/api/review-workflows/applicable", async (Guid projectId, ReviewSubject type,
            HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var workflow = await ActiveAsync(db, projectId, type, ct);
            if (workflow is null) return Results.Ok(new { required = false });

            var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => x.ProgramId).SingleAsync(ct);
            var eligible = await (from membership in db.ProgramMemberships.AsNoTracking().Where(x => x.ProgramId == programId && x.EndedAt == null)
                                  join account in db.UserAccounts.AsNoTracking().Where(x => x.State == AccountState.Active)
                                      on membership.UserId equals account.Id
                                  select new { account.UserName, account.DisplayName, membership.Role }).ToListAsync(ct);

            return Results.Ok(new
            {
                required = true,
                workflow.Id,
                workflow.Name,
                workflow.Version,
                mode = workflow.Mode.ToString(),
                stages = workflow.Stages.OrderBy(x => x.Position).Select(stage => new
                {
                    stage.Position,
                    stage.Name,
                    requiredRole = stage.RequiredRole.ToString(),
                    // Administrators are listed for every stage because they can stand in when the named
                    // authority is unavailable; a review that cannot proceed at all is not a control.
                    candidates = eligible
                        .Where(x => x.Role == stage.RequiredRole || x.Role == ProgramRole.Administrator)
                        .Select(x => new { userId = x.UserName, name = x.DisplayName, role = x.Role.ToString() })
                        .DistinctBy(x => x.userId)
                        .OrderBy(x => x.name),
                }),
            });
        });
    }

    /// <summary>The active procedure for this kind of package, or null when the project records none.</summary>
    public static async Task<ReviewWorkflow?> ActiveAsync(AeroLinkDbContext db, Guid projectId,
        ReviewSubject subject, CancellationToken ct) =>
        await db.ReviewWorkflows.AsNoTracking().Include(x => x.Stages)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.AppliesTo == subject
                                       && x.State == ReviewWorkflowState.Active, ct);

    /// <summary>A change request names its subject by its type; a test change request by its discipline.</summary>
    public static ReviewSubject SubjectOf(ChangeRequestType type) =>
        type == ChangeRequestType.System ? ReviewSubject.System : ReviewSubject.Software;

    public static ReviewSubject SubjectOf(TestChangeReviewDiscipline discipline) => discipline switch
    {
        TestChangeReviewDiscipline.System => ReviewSubject.SystemTest,
        TestChangeReviewDiscipline.HighLevelSoftware => ReviewSubject.HighLevelSoftwareTest,
        _ => ReviewSubject.LowLevelSoftwareTest,
    };

    public static async Task<ReviewWorkflowSpecification?> ActiveSpecificationAsync(AeroLinkDbContext db,
        Guid projectId, ChangeRequestType type, CancellationToken ct) =>
        (await ActiveAsync(db, projectId, SubjectOf(type), ct))?.Specification();

    public static async Task<ReviewWorkflowSpecification?> ActiveSpecificationAsync(AeroLinkDbContext db,
        Guid projectId, TestChangeReviewDiscipline discipline, CancellationToken ct) =>
        (await ActiveAsync(db, projectId, SubjectOf(discipline), ct))?.Specification();

    /// <summary>
    /// The authority each user holds on the program owning this project.
    ///
    /// Somebody can hold several roles; the strongest is what they can sign as, because a person who is both
    /// an engineer and a configuration manager does not lose the second by also being the first.
    /// </summary>
    public static async Task<Dictionary<Guid, ProgramRole?>> AuthoritiesAsync(AeroLinkDbContext db,
        Guid projectId, IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
        if (programId is null || userIds.Count == 0) return [];
        var memberships = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.EndedAt == null && userIds.Contains(x.UserId))
            .Select(x => new { x.UserId, x.Role }).ToListAsync(ct);
        return userIds.ToDictionary(
            id => id,
            id => memberships.Where(x => x.UserId == id).Select(x => (ProgramRole?)x.Role).OrderByDescending(Rank).FirstOrDefault());
    }

    /// <summary>
    /// The authority one user actually uses to sign one configured stage.
    ///
    /// A person can hold several Program roles, and the strongest one is not necessarily the one a stage
    /// asks for: a TestLead who is also an Approver must still be able to sign the TestLead stage as a
    /// TestLead, and a Configuration Manager who is also a Program Manager signs the Configuration Manager
    /// stage as a Configuration Manager. Administrator remains a substitution authority for any stage.
    /// The resolved authority is frozen on the approval step, so the signature stays explainable after
    /// memberships change.
    /// </summary>
    public static async Task<ProgramRole?> StageAuthorityAsync(AeroLinkDbContext db, Guid projectId,
        Guid userId, ProgramRole requiredRole, CancellationToken ct)
    {
        var programId = await db.Projects.Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId)
            .SingleOrDefaultAsync(ct);
        if (programId is null) return null;
        var roles = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.UserId == userId && x.EndedAt == null)
            .Select(x => x.Role).ToListAsync(ct);
        if (roles.Contains(ProgramRole.Administrator)) return ProgramRole.Administrator;
        return roles.Contains(requiredRole) ? requiredRole : null;
    }

    private static int Rank(ProgramRole? role) => role switch
    {
        ProgramRole.Administrator => 7,
        ProgramRole.ProgramManager => 6,
        ProgramRole.ConfigurationManager => 5,
        ProgramRole.Approver => 4,
        ProgramRole.TestLead => 3,
        ProgramRole.Reviewer => 2,
        ProgramRole.TestEngineer => 1,
        _ => 0,
    };

    private static object Map(ReviewWorkflow x) => new
    {
        x.Id,
        x.LogicalId,
        x.ProjectId,
        x.Name,
        appliesTo = x.AppliesTo.ToString(),
        mode = x.Mode.ToString(),
        x.Version,
        state = x.State.ToString(),
        x.CreatedBy,
        x.CreatedAt,
        x.ActivatedAt,
        x.RetiredAt,
        stages = x.Stages.OrderBy(s => s.Position)
            .Select(s => new { s.Position, s.Name, requiredRole = s.RequiredRole.ToString() }),
    };
}

public sealed record ReviewWorkflowStageRequest(string Name, ProgramRole RequiredRole);
public sealed record CreateReviewWorkflowRequest(Guid ProjectId, string Name, ReviewSubject AppliesTo,
    ReviewMode Mode, List<ReviewWorkflowStageRequest> Stages);
public sealed record ReviseReviewWorkflowRequest(string Name, ReviewMode Mode, List<ReviewWorkflowStageRequest> Stages);
