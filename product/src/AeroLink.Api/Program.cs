using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddAeroLinkInfrastructure(builder.Configuration);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (builder.Configuration.GetValue<bool>("DemoData:Enabled")) await SeedData.EnsureSeededAsync(db);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "AeroLink API" }));

app.MapGet("/api/programs", async (AeroLinkDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Programs.AsNoTracking().Select(p => new { p.Id, p.Name, p.Code }).ToListAsync(ct)));

app.MapPost("/api/workspaces", async (CreateWorkspaceRequest request, AeroLinkDbContext db, CancellationToken ct) =>
{
    if (await db.Programs.AnyAsync(x => x.Code == request.ProgramCode.Trim().ToUpper(), ct))
        return Results.Conflict(new { error = "A program with that code already exists." });
    try
    {
        var program = new ProgramRecord(request.ProgramName, request.ProgramCode);
        var project = new ProjectRecord(program.Id, request.ProjectName, request.SoftwareProduct);
        var release = new SoftwareRelease(project.Id, request.InitialRelease, request.InitialReleaseIsReleased);
        db.AddRange(program, project, release);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/programs/{program.Id}", ApiMap.Workspace(program, project, release));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/workspaces", async (AeroLinkDbContext db, CancellationToken ct) =>
{
    var programs = await db.Programs.AsNoTracking().ToListAsync(ct);
    var projects = await db.Projects.AsNoTracking().ToListAsync(ct);
    var releases = await db.Releases.AsNoTracking().ToListAsync(ct);
    return Results.Ok(programs.Select(program => new
    {
        program = new { program.Id, program.Name, program.Code },
        projects = projects.Where(x => x.ProgramId == program.Id).Select(project => new
        {
            project = new { project.Id, project.Name, project.SoftwareProduct },
            releases = releases.Where(x => x.ProjectId == project.Id).OrderBy(x => x.Version)
                .Select(x => new { x.Id, x.Version, x.IsReleased })
        })
    }));
});

app.MapGet("/api/context", async (AeroLinkDbContext db, CancellationToken ct) => Results.Ok(new
{
    programs = await db.Programs.AsNoTracking().ToListAsync(ct),
    projects = await db.Projects.AsNoTracking().ToListAsync(ct),
    releases = await db.Releases.AsNoTracking().OrderBy(x => x.Version).ToListAsync(ct)
}));

app.MapGet("/api/scrs", async (Guid? projectId, IScrRepository repository, CancellationToken ct) =>
    Results.Ok((await repository.ListAsync(ct)).Where(x => projectId is null || x.ProjectId == projectId).Select(ApiMap.ScrSummary)));

app.MapGet("/api/scrs/{id:guid}", async (Guid id, IScrRepository repository, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct);
    return scr is null ? Results.NotFound() : Results.Ok(ApiMap.ScrDetail(scr));
});

app.MapPost("/api/scrs", async (CreateScrRequest request, IScrRepository repository, CancellationToken ct) =>
{
    try
    {
        var scr = new SystemChangeRequest(request.BaseNumber, 0, request.ProjectId, request.TargetReleaseId,
            request.Title, request.Problem, request.Analysis, request.Solution, request.AuthorId, DateTimeOffset.UtcNow);
        await repository.AddAsync(scr, ct); await repository.SaveAsync(ct);
        return Results.Created($"/api/scrs/{scr.Id}", ApiMap.ScrDetail(scr));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scrs/{id:guid}/requirements", async (Guid id, RequirementChangeRequest request, IScrRepository repository, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    try
    {
        scr.AddRequirementChange(request.ActorId, request.BaseNumber, request.Revision, request.Level, request.Kind,
            request.Statement, request.Rationale, request.VerificationMethod, DateTimeOffset.UtcNow);
        await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scrs/{id:guid}/submit", async (Guid id, SubmitReviewRequest request, IScrRepository repository, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    try
    {
        scr.SubmitForReview(request.ActorId, request.Approvers.Select(x => new ApproverSelection(x.UserId, x.Name)).ToList(), DateTimeOffset.UtcNow);
        await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scrs/{id:guid}/approve", async (Guid id, ActorRequest request, IScrRepository repository, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    try { scr.ApproveActiveStage(request.ActorId, DateTimeOffset.UtcNow); await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scrs/{id:guid}/request-changes", async (Guid id, RequestChangesRequest request, IScrRepository repository, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    try { scr.RequestChanges(request.ActorId, request.Reason, DateTimeOffset.UtcNow); await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/dashboard", async (Guid? projectId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var scrs = await db.SystemChangeRequests.AsNoTracking().Where(x => projectId == null || x.ProjectId == projectId).ToListAsync(ct);
    return Results.Ok(new { totalScrs = scrs.Count, draft = scrs.Count(x => x.State == ScrState.Draft), inReview = scrs.Count(x => x.State == ScrState.InReview), approved = scrs.Count(x => x.State == ScrState.Approved || x.State == ScrState.SelectedForBaseline) });
});

app.Run();

public partial class Program { }

record CreateScrRequest(string BaseNumber, Guid ProjectId, Guid TargetReleaseId, string Title, string Problem, string Analysis, string Solution, string AuthorId);
record CreateWorkspaceRequest(string ProgramName, string ProgramCode, string ProjectName, string SoftwareProduct, string InitialRelease, bool InitialReleaseIsReleased);
record RequirementChangeRequest(string ActorId, string BaseNumber, int Revision, RequirementLevel Level, RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod);
record ApproverRequest(string UserId, string Name);
record SubmitReviewRequest(string ActorId, List<ApproverRequest> Approvers);
record ActorRequest(string ActorId);
record RequestChangesRequest(string ActorId, string Reason);

static class ApiMap
{
    public static object Workspace(ProgramRecord program, ProjectRecord project, SoftwareRelease release) => new
    {
        program = new { program.Id, program.Name, program.Code },
        project = new { project.Id, project.Name, project.SoftwareProduct },
        release = new { release.Id, release.Version, release.IsReleased }
    };
    public static object ScrSummary(SystemChangeRequest x) => new { x.Id, x.DisplayNumber, x.Title, state = x.State.ToString(), x.AuthorId, x.TargetReleaseId, requirementCount = x.RequirementChanges.Count, x.UpdatedAt };
    public static object ScrDetail(SystemChangeRequest x) => new
    {
        x.Id, x.BaseNumber, x.Revision, x.DisplayNumber, x.ProjectId, x.TargetReleaseId, x.Title, x.Problem, x.Analysis, x.Solution, x.AuthorId,
        state = x.State.ToString(), x.CreatedAt, x.UpdatedAt,
        requirementChanges = x.RequirementChanges.Select(r => new { r.Id, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement, r.Rationale, r.VerificationMethod }),
        reviewCycles = x.ReviewCycles.OrderBy(c => c.Sequence).Select(c => new { c.Id, c.Sequence, state = c.State.ToString(), c.SnapshotHash, c.StartedAt, c.CompletedAt, c.ClosureReason, steps = c.Steps.OrderBy(s => s.Position).Select(s => new { s.Position, s.ApproverId, s.ApproverName, state = s.State.ToString(), s.DecidedAt }) }),
        audit = x.AuditEvents.OrderByDescending(a => a.OccurredAt).Select(a => new { a.EventType, a.ActorId, a.Detail, a.OccurredAt })
    };
}

static class SeedData
{
    public static async Task EnsureSeededAsync(AeroLinkDbContext db)
    {
        if (await db.Programs.AnyAsync()) return;
        var program = new ProgramRecord("Flight Management System", "FMS");
        var project = new ProjectRecord(program.Id, "FMS Software", "Flight Management Software");
        db.AddRange(program, project, new SoftwareRelease(project.Id, "3.2", true), new SoftwareRelease(project.Id, "3.3", false));
        await db.SaveChangesAsync();
    }
}
