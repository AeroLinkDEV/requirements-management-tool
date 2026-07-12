using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Releases;
using System.Security.Cryptography;
using System.Text;
using AeroLink.Infrastructure;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAeroLinkInfrastructure(builder.Configuration);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173", "http://127.0.0.1:5174").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
    if (db.Database.IsNpgsql()) await db.Database.MigrateAsync();
    else await db.Database.EnsureCreatedAsync();
    if (builder.Configuration.GetValue<bool>("DemoData:Enabled"))
        await scope.ServiceProvider.GetRequiredService<FmsShowcaseSeeder>().EnsureSeededAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "AeroLink API" }));

app.MapPost("/api/showcase/seed", async (FmsShowcaseSeeder seeder, CancellationToken ct) => Results.Ok(await seeder.EnsureSeededAsync(ct)));

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

app.MapGet("/api/scrs", async (Guid projectId, Guid? releaseId, int? page, int? pageSize, string? search, ScrState? state, IScrRepository repository, CancellationToken ct) =>
{
    var result = await repository.QueryAsync(new ScrQuery(projectId, page is null or 0 ? 1 : page.Value, pageSize is null or 0 ? 50 : pageSize.Value, search, state, releaseId), ct);
    return Results.Ok(new { result.Page, result.PageSize, result.TotalCount, result.TotalPages, items = result.Items.Select(ApiMap.ScrSummary) });
});

app.MapGet("/api/scrs/{id:guid}", async (Guid id, IScrRepository repository, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct);
    return scr is null ? Results.NotFound() : Results.Ok(ApiMap.ScrDetail(scr));
});

app.MapPut("/api/scrs/{id:guid}/draft", async (Guid id, UpdateScrDraftRequest request, IScrRepository repository, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    if (scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "This SCR changed after it was opened. Refresh it before saving.", code = "stale_version" });
    try
    {
        scr.UpdateDraft(request.ActorId, request.Title, request.Problem, request.Analysis, request.Solution,
            request.RequirementChanges.Select(x => new RequirementChangeDraft(x.BaseNumber, x.Revision, x.Level, x.Kind, x.Statement, x.Rationale, x.VerificationMethod)).ToList(), DateTimeOffset.UtcNow);
        await repository.SaveAsync(ct);
        return Results.Ok(ApiMap.ScrDetail(scr));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/requirement-changes", async (Guid projectId, int page, int pageSize, string? search, AeroLinkDbContext db, CancellationToken ct) =>
{
    page = Math.Max(1, page == 0 ? 1 : page);
    pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
    var source = db.RequirementChanges.AsNoTracking()
        .Where(x => db.SystemChangeRequests.Any(scr => scr.Id == x.ScrId && scr.ProjectId == projectId));
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        source = source.Where(x => EF.Functions.ILike(x.BaseNumber, $"%{term}%") || EF.Functions.ILike(x.Statement, $"%{term}%"));
    }
    var totalCount = await source.CountAsync(ct);
    var items = await source.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision)
        .Skip((page - 1) * pageSize).Take(pageSize)
        .Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + x.Revision, level = x.Level.ToString(), kind = x.Kind.ToString(), x.Statement, x.VerificationMethod, x.ScrId })
        .ToListAsync(ct);
    return Results.Ok(new { page, pageSize, totalCount, totalPages = (int)Math.Ceiling(totalCount / (double)pageSize), items });
});

// Historical discovery endpoints deliberately include every revision and lifecycle state.
app.MapGet("/api/history/scrs", async (Guid projectId, string? search, Guid? releaseId, Guid? baselineId, Guid? buildId,
    int page, int pageSize, AeroLinkDbContext db, CancellationToken ct) =>
{
    page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
    var source = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId);
    if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x =>
        x.BaseNumber.ToLower().Contains(q) || x.Title.ToLower().Contains(q) || x.Problem.ToLower().Contains(q) ||
        x.Analysis.ToLower().Contains(q) || x.Solution.ToLower().Contains(q)); }
    if (releaseId is not null) source = source.Where(x => x.TargetReleaseId == releaseId);
    var selectedBaselineId = baselineId;
    if (buildId is not null) selectedBaselineId = await db.SoftwareBuilds.Where(x => x.Id == buildId && x.ProjectId == projectId).Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);
    if (selectedBaselineId is not null) source = source.Where(x => db.BaselineSelections.Any(s => s.BaselineId == selectedBaselineId && s.ScrId == x.Id));
    var total = await source.CountAsync(ct);
    var ordered = db.Database.IsSqlite() ? source.OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision) : source.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.BaseNumber).ThenByDescending(x => x.Revision);
    var items = await ordered
        .Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision,
            x.BaseNumber, x.Revision, x.Title, state = x.State.ToString(), x.AuthorId, x.TargetReleaseId, requirementCount = x.RequirementChanges.Count, x.CreatedAt, x.UpdatedAt }).ToListAsync(ct);
    return Results.Ok(new { page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
});

app.MapGet("/api/history/requirements", async (Guid projectId, string? search, Guid? releaseId, Guid? baselineId, Guid? buildId,
    int page, int pageSize, AeroLinkDbContext db, CancellationToken ct) =>
{
    page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
    var scrs = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId);
    if (releaseId is not null) scrs = scrs.Where(x => x.TargetReleaseId == releaseId);
    var selectedBaselineId = baselineId;
    if (buildId is not null) selectedBaselineId = await db.SoftwareBuilds.Where(x => x.Id == buildId && x.ProjectId == projectId).Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);
    if (selectedBaselineId is not null) scrs = scrs.Where(x => db.BaselineSelections.Any(s => s.BaselineId == selectedBaselineId && s.ScrId == x.Id));
    var source = from r in db.RequirementChanges.AsNoTracking() join s in scrs on r.ScrId equals s.Id select new { r, s };
    if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x =>
        x.r.BaseNumber.ToLower().Contains(q) || x.r.Statement.ToLower().Contains(q) ||
        x.r.Rationale.ToLower().Contains(q) || x.s.Title.ToLower().Contains(q)); }
    var total = await source.CountAsync(ct);
    var ordered = db.Database.IsSqlite() ? source.OrderBy(x => x.r.BaseNumber).ThenByDescending(x => x.r.Revision) : source.OrderBy(x => x.r.BaseNumber).ThenByDescending(x => x.r.Revision).ThenByDescending(x => x.s.UpdatedAt);
    var items = await ordered
        .Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.r.Id, displayNumber = x.r.BaseNumber + "." + (x.r.Revision < 10 ? "0" : "") + x.r.Revision,
            x.r.BaseNumber, x.r.Revision, level = x.r.Level.ToString(), kind = x.r.Kind.ToString(), x.r.Statement, x.r.Rationale, x.r.VerificationMethod,
            scrId = x.s.Id, scrDisplayNumber = x.s.BaseNumber + "." + (x.s.Revision < 10 ? "0" : "") + x.s.Revision, scrTitle = x.s.Title, scrState = x.s.State.ToString(), x.s.TargetReleaseId }).ToListAsync(ct);
    return Results.Ok(new { page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
});

app.MapGet("/api/builds", async (Guid projectId, string? search, AeroLinkDbContext db, CancellationToken ct) =>
{
    var source = db.SoftwareBuilds.AsNoTracking().Where(x => x.ProjectId == projectId);
    if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.BuildNumber.ToLower().Contains(q) || x.Description.ToLower().Contains(q)); }
    var joined = from build in source join release in db.Releases.AsNoTracking() on build.ReleaseId equals release.Id join baseline in db.CandidateBaselines.AsNoTracking() on build.BaselineId equals baseline.Id
        select new { build.Id, build.BuildNumber, build.Description, state = build.State.ToString(), build.RecordedBy, build.RecordedAt, build.ReleasedAt,
            releaseId = release.Id, release.Version, baselineId = baseline.Id, baselineDisplayNumber = baseline.BaseNumber + "." + (baseline.Revision < 10 ? "0" : "") + baseline.Revision,
            baseline.ContentHash, scrCount = baseline.Selections.Count };
    var items = await (db.Database.IsSqlite() ? joined.OrderByDescending(x => x.BuildNumber) : joined.OrderByDescending(x => x.RecordedAt)).ToListAsync(ct);
    return Results.Ok(items);
});

app.MapPost("/api/builds", async (CreateBuildRequest request, AeroLinkDbContext db, CancellationToken ct) =>
{
    var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.BaselineId, ct);
    if (baseline is null) return Results.NotFound();
    if (baseline.State != CandidateBaselineState.Frozen) return Results.BadRequest(new { error = "A build can only reference a frozen baseline." });
    if (baseline.RequirementsMaterializedAt is null) return Results.BadRequest(new { error = "Materialize the authoritative requirement baseline before recording a software build." });
    if (baseline.ProjectId != request.ProjectId || baseline.ReleaseId != request.ReleaseId) return Results.BadRequest(new { error = "Build, release, and baseline must belong to the same project context." });
    if (await db.SoftwareBuilds.AnyAsync(x => x.ProjectId == request.ProjectId && x.BuildNumber == request.BuildNumber.Trim(), ct)) return Results.Conflict(new { error = "That build number already exists in this project." });
    try { var build = new SoftwareBuild(request.ProjectId, request.ReleaseId, request.BaselineId, request.BuildNumber, request.Description, request.RecordedBy, DateTimeOffset.UtcNow);
        db.SoftwareBuilds.Add(build); await db.SaveChangesAsync(ct); return Results.Created($"/api/builds/{build.Id}", new { build.Id, build.BuildNumber }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/builds/{id:guid}", async (Guid id, AeroLinkDbContext db, CancellationToken ct) =>
{
    var build = await db.SoftwareBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (build is null) return Results.NotFound();
    var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == build.BaselineId, ct);
    var scrIds = await db.BaselineSelections.AsNoTracking().Where(x => x.BaselineId == baseline.Id).Select(x => x.ScrId).ToListAsync(ct);
    var scrs = await db.SystemChangeRequests.AsNoTracking().Where(x => scrIds.Contains(x.Id)).Include(x => x.RequirementChanges).OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision).ToListAsync(ct);
    var effectiveRequirements = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id)
                                       join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                                       join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                                       orderby artifact.BaseNumber select new { artifact.Id, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                           level = artifact.Level.ToString(), revision.Statement, revision.VerificationMethod }).ToListAsync(ct);
    return Results.Ok(new { build.Id, build.BuildNumber, build.Description, state = build.State.ToString(), build.RecordedBy, build.RecordedAt, build.ReleasedAt,
        build.ProjectId, build.ReleaseId, baseline = new { baseline.Id, baseline.DisplayNumber, baseline.Name, baseline.ContentHash, baseline.FrozenAt },
        effectiveRequirements, scrs = scrs.Select(x => new { x.Id, x.DisplayNumber, x.Title, state = x.State.ToString(), requirements = x.RequirementChanges.OrderBy(r => r.BaseNumber).ThenByDescending(r => r.Revision).Select(r => new { r.Id, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement }) }) });
});

app.MapPost("/api/scrs", async (CreateScrRequest request, IScrRepository repository, CancellationToken ct) =>
{
    try
    {
        var scr = new SystemChangeRequest(request.BaseNumber, 0, request.ProjectId, request.TargetReleaseId,
            request.Title, request.Problem, request.Analysis, request.Solution, request.AuthorId, DateTimeOffset.UtcNow, request.Type);
        await repository.AddAsync(scr, ct); await repository.SaveAsync(ct);
        return Results.Created($"/api/scrs/{scr.Id}", ApiMap.ScrDetail(scr));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scr-drafts", async (CreateScrDraftRequest request, IScrRepository repository, CancellationToken ct) =>
{
    try
    {
        var now = DateTimeOffset.UtcNow;
        var scr = new SystemChangeRequest(request.BaseNumber, 0, request.ProjectId, request.TargetReleaseId,
            request.Title, request.Problem, request.Analysis, request.Solution, request.AuthorId, now, request.Type);
        foreach (var change in request.RequirementChanges)
            scr.AddRequirementChange(request.AuthorId, change.BaseNumber, change.Revision, change.Level, change.Kind,
                change.Statement, change.Rationale, change.VerificationMethod, now);
        await repository.AddAsync(scr, ct);
        await repository.SaveAsync(ct);
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
    if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "This SCR changed after it was opened. Refresh it before submitting.", code = "stale_version" });
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
    if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "The review advanced after this page was loaded. Refresh before acting.", code = "stale_version" });
    try { scr.ApproveActiveStage(request.ActorId, DateTimeOffset.UtcNow); await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/scrs/{id:guid}/request-changes", async (Guid id, RequestChangesRequest request, IScrRepository repository, CancellationToken ct) =>
{
    var scr = await repository.GetAsync(id, ct); if (scr is null) return Results.NotFound();
    if (request.ExpectedVersion is not null && scr.Version != request.ExpectedVersion) return Results.Conflict(new { error = "The review advanced after this page was loaded. Refresh before acting.", code = "stale_version" });
    try { scr.RequestChanges(request.ActorId, request.Reason, DateTimeOffset.UtcNow); await repository.SaveAsync(ct); return Results.Ok(ApiMap.ScrDetail(scr)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/baselines", async (Guid projectId, Guid releaseId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var items = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId)
        .OrderBy(x => x.BaseNumber).ThenByDescending(x => x.Revision).Select(x => new { x.Id, x.BaseNumber, x.Revision, x.Name, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, selectionCount = x.Selections.Count }).ToListAsync(ct);
    return Results.Ok(items.Select(x => new { x.Id, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.Name, x.state, x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, x.selectionCount }));
});

app.MapPost("/api/baselines", async (CreateBaselineRequest request, IBaselineRepository repository, CancellationToken ct) =>
{
    try
    {
        var baseline = new CandidateBaseline(request.BaseNumber, request.Revision, request.ProjectId, request.ReleaseId,
            request.PredecessorBaselineId, request.Name, request.ActorId, DateTimeOffset.UtcNow);
        await repository.AddAsync(baseline, ct); await repository.SaveAsync(ct);
        return Results.Created($"/api/baselines/{baseline.Id}", ApiMap.Baseline(baseline));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/baselines/{id:guid}", async (Guid id, IBaselineRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
{
    var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
    var scrIds = baseline.Selections.Select(x => x.ScrId).ToList();
    var selected = await db.SystemChangeRequests.AsNoTracking().Where(x => scrIds.Contains(x.Id))
        .Include(x => x.RequirementChanges).ToListAsync(ct);
    return Results.Ok(ApiMap.BaselineDetail(baseline, selected));
});

app.MapGet("/api/baselines/{id:guid}/eligible-scrs", async (Guid id, IBaselineRepository repository, AeroLinkDbContext db, CancellationToken ct) =>
{
    var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
    var items = await db.SystemChangeRequests.AsNoTracking()
        .Where(x => x.ProjectId == baseline.ProjectId && x.TargetReleaseId == baseline.ReleaseId && x.State == ScrState.Approved)
        .OrderBy(x => x.BaseNumber).Select(x => new { x.Id, displayNumber = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision, x.Title, requirementCount = x.RequirementChanges.Count, x.UpdatedAt }).ToListAsync(ct);
    return Results.Ok(items);
});

app.MapPost("/api/baselines/{id:guid}/selections", async (Guid id, BaselineSelectionRequest request, IBaselineRepository baselines, IScrRepository scrs, CancellationToken ct) =>
{
    var baseline = await baselines.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
    var scr = await scrs.GetAsync(request.ScrId, ct); if (scr is null) return Results.NotFound();
    try { baseline.Select(scr, request.ActorId, DateTimeOffset.UtcNow); await baselines.SaveAsync(ct); return Results.Ok(ApiMap.Baseline(baseline)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/baselines/{id:guid}/selections/{scrId:guid}", async (Guid id, Guid scrId, string actorId, IBaselineRepository baselines, IScrRepository scrs, CancellationToken ct) =>
{
    var baseline = await baselines.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
    var scr = await scrs.GetAsync(scrId, ct); if (scr is null) return Results.NotFound();
    try { baseline.Remove(scr, actorId, DateTimeOffset.UtcNow); await baselines.SaveAsync(ct); return Results.NoContent(); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/baselines/{id:guid}/freeze", async (Guid id, BaselineActorRequest request, IBaselineRepository repository, CancellationToken ct) =>
{
    var baseline = await repository.GetAsync(id, ct); if (baseline is null) return Results.NotFound();
    try { baseline.Freeze(request.ActorId, DateTimeOffset.UtcNow); await repository.SaveAsync(ct); return Results.Ok(ApiMap.Baseline(baseline)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/baselines/{id:guid}/materialize-requirements", async (Guid id, BaselineActorRequest request, RequirementBaselineMaterializer materializer, CancellationToken ct) =>
{
    try { return Results.Ok(await materializer.MaterializeAsync(id, request.ActorId, DateTimeOffset.UtcNow, ct)); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/baselines/{id:guid}/swrd", async (Guid id, AeroLinkDbContext db, CancellationToken ct) =>
{
    var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (baseline is null) return Results.NotFound();
    var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == baseline.ReleaseId, ct);
    var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == baseline.ProjectId, ct);
    var rows = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == id)
                      join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                      join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                      join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceScrId equals scr.Id
                      orderby artifact.BaseNumber
                      select new { artifact.Id, artifact.BaseNumber, revisionId = revision.Id, revision.Revision, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                          level = artifact.Level.ToString(), revision.Statement, revision.Rationale, revision.VerificationMethod, sourceScrId = scr.Id,
                          sourceScr = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision }).ToListAsync(ct);
    return Results.Ok(new { documentType = "SWRD", title = $"{project.SoftwareProduct} Software Requirements Document", release = release.Version,
        baseline = baseline.DisplayNumber, baseline.Name, baseline.RequirementsHash, baseline.RequirementsMaterializedAt, requirementCount = rows.Count, requirements = rows });
});

app.MapGet("/api/requirements", async (Guid projectId, string? search, Guid? releaseId, Guid? baselineId, bool includeRetired, int page, int pageSize, AeroLinkDbContext db, CancellationToken ct) =>
{
    page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
    var source = from artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId)
                 join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId
                 join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceScrId equals scr.Id
                 select new { artifact, revision, scr };
    if (baselineId is not null) source = source.Where(x => db.BaselineRequirements.Any(m => m.BaselineId == baselineId && m.RevisionId == x.revision.Id));
    else if (!includeRetired) source = source.Where(x => x.revision.State == AeroLink.Domain.Requirements.RequirementRevisionState.Active);
    if (releaseId is not null) source = source.Where(x => db.CandidateBaselines.Any(b => b.Id == x.revision.EffectiveBaselineId && b.ReleaseId == releaseId));
    if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.artifact.BaseNumber.ToLower().Contains(q) || x.revision.Statement.ToLower().Contains(q) || x.revision.Rationale.ToLower().Contains(q)); }
    var total = await source.CountAsync(ct);
    var items = await source.OrderBy(x => x.artifact.BaseNumber).ThenByDescending(x => x.revision.Revision).Skip((page - 1) * pageSize).Take(pageSize)
        .Select(x => new { x.artifact.Id, x.artifact.BaseNumber, level = x.artifact.Level.ToString(), revisionId = x.revision.Id, x.revision.Revision,
            displayNumber = x.artifact.BaseNumber + "." + (x.revision.Revision < 10 ? "0" : "") + x.revision.Revision, x.revision.Statement, x.revision.Rationale,
            x.revision.VerificationMethod, state = x.revision.State.ToString(), x.revision.EffectiveBaselineId, sourceScrId = x.scr.Id,
            sourceScr = x.scr.BaseNumber + "." + (x.scr.Revision < 10 ? "0" : "") + x.scr.Revision, x.revision.CreatedAt }).ToListAsync(ct);
    return Results.Ok(new { page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
});

app.MapGet("/api/requirements/{id:guid}/history", async (Guid id, AeroLinkDbContext db, CancellationToken ct) =>
{
    var artifact = await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (artifact is null) return Results.NotFound();
    var revisions = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => x.ArtifactId == id)
                           join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceScrId equals scr.Id
                           join baseline in db.CandidateBaselines.AsNoTracking() on revision.EffectiveBaselineId equals baseline.Id
                           orderby revision.Revision descending select new { revision.Id, revision.Revision, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                               revision.Statement, revision.Rationale, revision.VerificationMethod, state = revision.State.ToString(), revision.CreatedAt,
                               sourceScrId = scr.Id, sourceScr = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision,
                               baselineId = baseline.Id, baseline = baseline.BaseNumber + "." + (baseline.Revision < 10 ? "0" : "") + baseline.Revision }).ToListAsync(ct);
    return Results.Ok(new { artifact.Id, artifact.BaseNumber, level = artifact.Level.ToString(), revisions });
});

app.MapGet("/api/showcase/overview", async (Guid projectId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Version).ToListAsync(ct);
    var releasedIds = releases.Where(x => x.IsReleased).Select(x => x.Id).ToArray(); var activeIds = releases.Where(x => !x.IsReleased).Select(x => x.Id).ToArray();
    var requests = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId);
    var requirements = db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId);
    return Results.Ok(new {
        releases = releases.Select(x => new { x.Id, x.Version, x.IsReleased }),
        systemRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.System, ct),
        highLevelRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.HighLevel, ct),
        lowLevelRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.LowLevel, ct),
        historicalScrs = await requests.CountAsync(x => x.Type == ChangeRequestType.System && releasedIds.Contains(x.TargetReleaseId), ct),
        historicalSwcrs = await requests.CountAsync(x => x.Type == ChangeRequestType.Software && releasedIds.Contains(x.TargetReleaseId), ct),
        activeRequests = await requests.CountAsync(x => activeIds.Contains(x.TargetReleaseId), ct),
        traceLinks = await db.RequirementTraces.CountAsync(x => x.ProjectId == projectId, ct),
        testProcedures = await db.TestProcedures.CountAsync(x => x.ProjectId == projectId, ct),
        testExecutions = await db.TestExecutions.CountAsync(x => x.ProjectId == projectId, ct),
        controlledDocuments = await db.ControlledDocuments.CountAsync(x => x.ProjectId == projectId, ct),
        softwareBuilds = await db.SoftwareBuilds.CountAsync(x => x.ProjectId == projectId, ct)
    });
});

app.MapGet("/api/documents", async (Guid projectId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var rows = await (from document in db.ControlledDocuments.AsNoTracking().Where(x => x.ProjectId == projectId)
                      join release in db.Releases.AsNoTracking() on document.ReleaseId equals release.Id
                      join baseline in db.CandidateBaselines.AsNoTracking() on document.BaselineId equals baseline.Id
                      orderby document.Type select new { document.Id, type = document.Type.ToString(), document.DocumentNumber, document.Revision, document.Title,
                          document.ContentHash, document.ArtifactCount, document.GeneratedAt, release = release.Version,
                          baselineId = baseline.Id, baseline = baseline.BaseNumber + "." + (baseline.Revision < 10 ? "0" : "") + baseline.Revision }).ToListAsync(ct);
    return Results.Ok(rows.Select(x => new { x.Id, x.type, displayNumber = x.DocumentNumber + "." + x.Revision.ToString("D2"), x.Title, x.ContentHash, x.ArtifactCount, x.GeneratedAt, x.release, x.baselineId, x.baseline }));
});

app.MapGet("/api/documents/{id:guid}/download", async (Guid id, string? format, ControlledOutputGenerator generator, CancellationToken ct) =>
{
    var output = await generator.GenerateAsync(id, format ?? "docx", ct); return output is null ? Results.NotFound() : Results.File(output.Content, output.ContentType, output.FileName);
});

app.MapPost("/api/baselines/{id:guid}/generate-documents", async (Guid id, GenerateDocumentsRequest request, AeroLinkDbContext db, CancellationToken ct) =>
{
    var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (baseline is null) return Results.NotFound(); if (baseline.RequirementsMaterializedAt is null) return Results.BadRequest(new { error = "Materialize the requirement baseline before generating controlled outputs." });
    var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == baseline.ReleaseId, ct); var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == baseline.ProjectId, ct);
    var requirementCounts = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == id) join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id group artifact by artifact.Level into g select new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    var testCounts = await db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == project.Id).GroupBy(x => x.Level).Select(x => new { x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    var suffix = release.Version.Replace(".", ""); var specs = new[] {
        (ControlledDocumentType.Sysrd,$"SYSRD-{int.Parse(suffix):D8}",$"{project.SoftwareProduct} System Requirements Document",requirementCounts.GetValueOrDefault(RequirementLevel.System)),
        (ControlledDocumentType.SwrdHighLevel,$"HLRD-{int.Parse(suffix):D8}",$"{project.SoftwareProduct} High-Level Software Requirements Document",requirementCounts.GetValueOrDefault(RequirementLevel.HighLevel)),
        (ControlledDocumentType.SwrdLowLevel,$"LLRD-{int.Parse(suffix):D8}",$"{project.SoftwareProduct} Low-Level Software Requirements Document",requirementCounts.GetValueOrDefault(RequirementLevel.LowLevel)),
        (ControlledDocumentType.SystemTestProcedures,$"SYSTD-{int.Parse(suffix):D8}",$"{project.SoftwareProduct} System Test Procedures",testCounts.GetValueOrDefault(TestProcedureLevel.System)),
        (ControlledDocumentType.HighLevelTestProcedures,$"HLRTD-{int.Parse(suffix):D8}",$"{project.SoftwareProduct} HLR Test Procedures",testCounts.GetValueOrDefault(TestProcedureLevel.HighLevel)),
        (ControlledDocumentType.LowLevelTestProcedures,$"LLRTD-{int.Parse(suffix):D8}",$"{project.SoftwareProduct} LLR Test Procedures",testCounts.GetValueOrDefault(TestProcedureLevel.LowLevel)) };
    var existing = await db.ControlledDocuments.Where(x => x.BaselineId == id).ToListAsync(ct); foreach (var spec in specs.Where(s => existing.All(x => x.Type != s.Item1))) { var content = $"{baseline.RequirementsHash}|{spec.Item1}|{spec.Item4}|{request.ActorId}"; var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(); db.ControlledDocuments.Add(new ControlledDocument(project.Id, release.Id, baseline.Id, spec.Item1, spec.Item2, spec.Item3, 0, hash, spec.Item4, DateTimeOffset.UtcNow)); }
    await db.SaveChangesAsync(ct); return Results.Ok(new { generated = await db.ControlledDocuments.CountAsync(x => x.BaselineId == id, ct) });
});

app.MapGet("/api/release-campaigns", async (Guid projectId, AeroLinkDbContext db, ReleaseReadinessService readiness, CancellationToken ct) =>
{
    var campaigns = (await db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).ToList(); var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Id, ct);
    var output = new List<object>(); foreach (var campaign in campaigns) { var status = await readiness.CalculateAsync(campaign.Id, ct); output.Add(new { campaign.Id, campaign.Name, state = campaign.State.ToString(), campaign.ReleaseId, release = releases[campaign.ReleaseId].Version, campaign.BaselineId, campaign.SoftwareBuildId, campaign.OwnerId, campaign.CreatedAt, campaign.ReleasedAt, campaign.ReleaseHash, readiness = status }); }
    return Results.Ok(output);
});

app.MapGet("/api/release-campaigns/{id:guid}", async (Guid id, AeroLinkDbContext db, ReleaseReadinessService readiness, CancellationToken ct) =>
{
    var campaign = await db.ReleaseCampaigns.AsNoTracking().Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
    var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == campaign.ReleaseId, ct); var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == campaign.BaselineId, ct);
    var impacts = await (from impact in db.ImpactDispositions.AsNoTracking().Where(x => x.CampaignId == id) join scr in db.SystemChangeRequests.AsNoTracking() on impact.ScrId equals scr.Id orderby scr.BaseNumber, impact.Kind select new { impact.Id, impact.ScrId, scr = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision, scr.Title, kind = impact.Kind.ToString(), impact.ArtifactReference, impact.Description, state = impact.State.ToString(), impact.Rationale, impact.DispositionedBy, impact.DispositionedAt }).ToListAsync(ct);
    return Results.Ok(new { campaign.Id, campaign.Name, state = campaign.State.ToString(), campaign.ProjectId, campaign.ReleaseId, release = release.Version, campaign.BaselineId, baseline = baseline.DisplayNumber, baselineState = baseline.State.ToString(), baseline.RequirementsHash, campaign.SoftwareBuildId, campaign.OwnerId, campaign.CreatedAt, campaign.ReleasedAt, campaign.ReleaseHash,
        readiness = await readiness.CalculateAsync(id, ct), impacts, approvals = campaign.Approvals.OrderBy(x => x.Position).Select(x => new { x.Position, x.ApproverId, x.ApproverName, state = x.State.ToString(), x.ApprovedAt }), events = campaign.Events.OrderByDescending(x => x.OccurredAt).Select(x => new { x.EventType, x.ActorId, x.Detail, x.OccurredAt }) });
});

app.MapPut("/api/impact-dispositions/{id:guid}", async (Guid id, DispositionImpactRequest request, AeroLinkDbContext db, CancellationToken ct) =>
{
    var impact = await db.ImpactDispositions.SingleOrDefaultAsync(x => x.Id == id, ct); if (impact is null) return Results.NotFound();
    try { impact.Disposition(request.State, request.Rationale, request.ActorId, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Results.NoContent(); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/release-campaigns/{id:guid}/verification-build", async (Guid id, SelectBuildRequest request, AeroLinkDbContext db, CancellationToken ct) =>
{
    var campaign = await db.ReleaseCampaigns.Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
    if (!await db.SoftwareBuilds.AnyAsync(x => x.Id == request.SoftwareBuildId && x.ProjectId == campaign.ProjectId && x.ReleaseId == campaign.ReleaseId, ct)) return Results.BadRequest(new { error = "Select a software build from this campaign release." });
    try { campaign.SelectVerificationBuild(request.SoftwareBuildId, request.ActorId, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Results.NoContent(); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/release-campaigns/{id:guid}/review", async (Guid id, StartReleaseReviewRequest request, AeroLinkDbContext db, ReleaseReadinessService readiness, CancellationToken ct) =>
{
    var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound(); var status = await readiness.CalculateAsync(id, ct);
    var blockers = status.Gates.Where(x => x.Code != "release_approval" && !x.Complete).Select(x => x.Name).ToList(); if (blockers.Count > 0) return Results.BadRequest(new { error = "Resolve readiness gates before release review.", blockers });
    try { campaign.BeginReleaseReview(request.ActorId, request.Approvers.Select(x => (x.UserId, x.Name)).ToList(), DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Results.NoContent(); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/release-campaigns/{id:guid}/approve", async (Guid id, CampaignActorRequest request, AeroLinkDbContext db, CancellationToken ct) =>
{
    var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
    try { var complete = campaign.Approve(request.ActorId, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Results.Ok(new { complete }); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/release-campaigns/{id:guid}/release", async (Guid id, CompleteReleaseRequest request, AeroLinkDbContext db, ReleaseReadinessService readiness, CancellationToken ct) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct); var campaign = await db.ReleaseCampaigns.Include(x => x.Approvals).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == id, ct); if (campaign is null) return Results.NotFound();
    var status = await readiness.CalculateAsync(id, ct); if (!status.ReadyForRelease) return Results.BadRequest(new { error = "Every release-readiness gate must be complete.", blockers = status.Gates.Where(x => !x.Complete).Select(x => x.Name) });
    if (campaign.SoftwareBuildId is null) return Results.BadRequest(new { error = "Select the verified release build." });
    var baseline = await db.CandidateBaselines.Include(x => x.Events).SingleAsync(x => x.Id == campaign.BaselineId, ct); var release = await db.Releases.SingleAsync(x => x.Id == campaign.ReleaseId, ct); var build = await db.SoftwareBuilds.SingleAsync(x => x.Id == campaign.SoftwareBuildId, ct);
    var documents = await db.ControlledDocuments.AsNoTracking().Where(x => x.BaselineId == baseline.Id).OrderBy(x => x.Type).Select(x => x.ContentHash).ToListAsync(ct); var manifest = string.Join("|", baseline.ContentHash, baseline.RequirementsHash, build.BuildNumber, string.Join(";", documents)); var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
    try { campaign.Release(build.Id, hash, request.ActorId, DateTimeOffset.UtcNow); baseline.MarkReleased(request.ActorId, DateTimeOffset.UtcNow); release.MarkReleased(DateTimeOffset.UtcNow); build.MarkReleased(DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.Ok(new { release = release.Version, build.BuildNumber, releaseHash = hash }); }
    catch (Exception ex) when (ex is DomainException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/evidence", async (HttpRequest http, AeroLinkDbContext db, EvidenceFileStore store, CancellationToken ct) =>
{
    if (!http.HasFormContentType) return Results.BadRequest(new { error = "Use multipart form data." }); var form = await http.ReadFormAsync(ct); var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a non-empty evidence file." }); if (!Guid.TryParse(form["projectId"], out var projectId) || !await db.Projects.AnyAsync(x => x.Id == projectId, ct)) return Results.BadRequest(new { error = "A valid project is required." }); var uploadedBy = form["uploadedBy"].ToString();
    try { await using var stream = file.OpenReadStream(); var stored = await store.StoreAsync(stream, file.FileName, file.ContentType, ct); var evidence = new EvidenceRecord(projectId, stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256, stored.StorageKey, uploadedBy, DateTimeOffset.UtcNow); db.EvidenceRecords.Add(evidence); await db.SaveChangesAsync(ct); return Results.Created($"/api/evidence/{evidence.Id}", new { evidence.Id, evidence.OriginalFileName, evidence.ContentType, evidence.Size, evidence.Sha256, evidence.UploadedBy, evidence.UploadedAt }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).DisableAntiforgery();

app.MapPost("/api/test-executions/{executionId:guid}/evidence/{evidenceId:guid}", async (Guid executionId, Guid evidenceId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var executionProject = await db.TestExecutions.AsNoTracking().Where(x => x.Id == executionId).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
    var evidenceProject = await db.EvidenceRecords.AsNoTracking().Where(x => x.Id == evidenceId).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
    if (executionProject is null || evidenceProject is null) return Results.NotFound();
    if (executionProject != evidenceProject) return Results.BadRequest(new { error = "Evidence and execution must belong to the same project." });
    if (await db.TestExecutionEvidence.AnyAsync(x => x.TestExecutionId == executionId && x.EvidenceId == evidenceId, ct)) return Results.Conflict(new { error = "Evidence is already linked." }); db.TestExecutionEvidence.Add(new TestExecutionEvidence(executionId, evidenceId)); await db.SaveChangesAsync(ct); return Results.NoContent();
});

app.MapGet("/api/evidence/{id:guid}", async (Guid id, AeroLinkDbContext db, EvidenceFileStore store, CancellationToken ct) =>
{
    var evidence = await db.EvidenceRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); return evidence is null ? Results.NotFound() : Results.File(store.OpenRead(evidence.StorageKey), evidence.ContentType, evidence.OriginalFileName, enableRangeProcessing: true);
});

app.MapPost("/api/trace-links", async (CreateTraceLinkRequest request, AeroLinkDbContext db, CancellationToken ct) =>
{
    var revisions = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => x.Id == request.SourceRevisionId || x.Id == request.TargetRevisionId)
                           join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                           select new { revision.Id, artifact.ProjectId }).ToListAsync(ct);
    if (revisions.Count != 2) return Results.BadRequest(new { error = "Both exact requirement revisions must exist." });
    if (revisions.Any(x => x.ProjectId != request.ProjectId)) return Results.BadRequest(new { error = "Both revisions must belong to the selected project." });
    try { var link = new RequirementTraceLink(request.ProjectId, request.SourceRevisionId, request.TargetRevisionId, request.Type, request.Rationale, DateTimeOffset.UtcNow); db.RequirementTraces.Add(link); await db.SaveChangesAsync(ct); return Results.Created($"/api/trace-links/{link.Id}", new { link.Id }); }
    catch (Exception ex) when (ex is DomainException or DbUpdateException) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/trace-links/{id:guid}", async (Guid id, AeroLinkDbContext db, CancellationToken ct) =>
{ var link = await db.RequirementTraces.SingleOrDefaultAsync(x => x.Id == id, ct); if (link is null) return Results.NotFound(); db.RequirementTraces.Remove(link); await db.SaveChangesAsync(ct); return Results.NoContent(); });

app.MapGet("/api/release-comparison", async (Guid projectId, Guid fromReleaseId, Guid toReleaseId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId && (x.Id == fromReleaseId || x.Id == toReleaseId)).ToListAsync(ct); if (releases.Count != 2) return Results.BadRequest(new { error = "Select two releases from the same project." });
    var baselines = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId && (x.ReleaseId == fromReleaseId || x.ReleaseId == toReleaseId)).ToListAsync(ct); var fromBaseline = baselines.Where(x => x.ReleaseId == fromReleaseId).OrderByDescending(x => x.FrozenAt).FirstOrDefault(); var toBaseline = baselines.Where(x => x.ReleaseId == toReleaseId).OrderByDescending(x => x.FrozenAt).FirstOrDefault();
    async Task<Dictionary<string, (int Revision, string Statement, string Level)>> Set(Guid? baselineId) { if (baselineId is null) return []; var rows = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId) join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id select new { artifact.BaseNumber, revision.Revision, revision.Statement, Level = artifact.Level.ToString() }).ToListAsync(ct); return rows.ToDictionary(x => x.BaseNumber, x => (x.Revision, x.Statement, x.Level)); }
    var effectiveAvailable = toBaseline?.RequirementsMaterializedAt is not null;
    var from = effectiveAvailable ? await Set(fromBaseline?.Id) : []; var to = effectiveAvailable ? await Set(toBaseline?.Id) : []; var keys = from.Keys.Union(to.Keys).OrderBy(x => x).ToList();
    var effective = keys.Select(key => { var hasFrom = from.TryGetValue(key, out var a); var hasTo = to.TryGetValue(key, out var b); var kind = !hasFrom ? "Added" : !hasTo ? "Retired" : a.Revision != b.Revision || a.Statement != b.Statement ? "Modified" : "Unchanged"; return new { baseNumber = key, kind, fromRevision = hasFrom ? a.Revision : (int?)null, toRevision = hasTo ? b.Revision : (int?)null, level = hasTo ? b.Level : a.Level }; }).ToList();
    var requests = await db.SystemChangeRequests.AsNoTracking().Where(x => x.TargetReleaseId == toReleaseId).Include(x => x.RequirementChanges).OrderBy(x => x.BaseNumber).ToListAsync(ct);
    var proposed = requests.SelectMany(scr => scr.RequirementChanges.Select(change => new { scrId = scr.Id, scr = scr.DisplayNumber, scr.Title, state = scr.State.ToString(), type = scr.Type.ToString(), change.DisplayNumber, level = change.Level.ToString(), kind = change.Kind.ToString(), change.Statement })).ToList();
    return Results.Ok(new { fromRelease = releases.Single(x => x.Id == fromReleaseId).Version, toRelease = releases.Single(x => x.Id == toReleaseId).Version,
        fromBaseline = fromBaseline?.DisplayNumber, toBaseline = toBaseline?.DisplayNumber, toMaterialized = effectiveAvailable,
        summary = new { added = effective.Count(x => x.kind == "Added"), modified = effective.Count(x => x.kind == "Modified"), retired = effective.Count(x => x.kind == "Retired"), unchanged = effective.Count(x => x.kind == "Unchanged"), proposed = proposed.Count }, effective, proposed });
});

app.MapGet("/api/traceability", async (Guid projectId, Guid? baselineId, string? search, int page, int pageSize, AeroLinkDbContext db, CancellationToken ct) =>
{
    page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
    if (baselineId is null) baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId && x.RequirementsMaterializedAt != null).OrderByDescending(x => x.FrozenAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
    if (baselineId is null) return Results.Ok(new { page, pageSize, totalCount = 0, items = Array.Empty<object>() });
    var source = from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                 join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                 join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                 select new { artifact, revision };
    if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.artifact.BaseNumber.ToLower().Contains(q) || x.revision.Statement.ToLower().Contains(q)); }
    var total = await source.CountAsync(ct); var selected = await source.OrderBy(x => x.artifact.BaseNumber).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
    var selectedIds = selected.Select(x => x.revision.Id).ToList(); var links = await db.RequirementTraces.AsNoTracking().Where(x => selectedIds.Contains(x.SourceRevisionId) || selectedIds.Contains(x.TargetRevisionId)).ToListAsync(ct);
    var relatedIds = links.SelectMany(x => new[] { x.SourceRevisionId, x.TargetRevisionId }).Distinct().ToList();
    var related = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => relatedIds.Contains(x.Id)) join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id select new { revision.Id, artifact.BaseNumber, revision.Revision, level = artifact.Level.ToString() }).ToDictionaryAsync(x => x.Id, ct);
    var coverage = await db.TestCoverage.AsNoTracking().Where(x => selectedIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
    var items = selected.Select(x => new { x.artifact.Id, revisionId = x.revision.Id, displayNumber = x.artifact.BaseNumber + "." + x.revision.Revision.ToString("D2"), level = x.artifact.Level.ToString(), x.revision.Statement,
        parents = links.Where(l => l.SourceRevisionId == x.revision.Id).Select(l => new { id = l.TargetRevisionId, displayNumber = related[l.TargetRevisionId].BaseNumber + "." + related[l.TargetRevisionId].Revision.ToString("D2"), related[l.TargetRevisionId].level, type = l.Type.ToString() }),
        children = links.Where(l => l.TargetRevisionId == x.revision.Id).Select(l => new { id = l.SourceRevisionId, displayNumber = related[l.SourceRevisionId].BaseNumber + "." + related[l.SourceRevisionId].Revision.ToString("D2"), related[l.SourceRevisionId].level, type = l.Type.ToString() }),
        testCount = coverage.Count(c => c.RequirementRevisionId == x.revision.Id) });
    return Results.Ok(new { baselineId, page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
});

app.MapGet("/api/dashboard", async (Guid? projectId, Guid? releaseId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var source = db.SystemChangeRequests.AsNoTracking().Where(x => (projectId == null || x.ProjectId == projectId) && (releaseId == null || x.TargetReleaseId == releaseId));
    return Results.Ok(new {
        totalScrs = await source.CountAsync(ct),
        draft = await source.CountAsync(x => x.State == ScrState.Draft, ct),
        inReview = await source.CountAsync(x => x.State == ScrState.InReview, ct),
        approved = await source.CountAsync(x => x.State == ScrState.Approved || x.State == ScrState.SelectedForBaseline, ct)
    });
});

app.MapGet("/api/test-procedures", async (Guid projectId, string? search, AeroLinkDbContext db, CancellationToken ct) =>
{
    var source = db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId);
    if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.BaseNumber.ToLower().Contains(q) || x.Title.ToLower().Contains(q)); }
    var items = await source.OrderBy(x => x.BaseNumber).Select(x => new { x.Id, x.BaseNumber, x.Title, x.OwnerId, x.Level, x.CreatedAt }).ToListAsync(ct);
    var ids = items.Select(x => x.Id).ToList(); var revisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => ids.Contains(x.ProcedureId)).ToListAsync(ct);
    var revisionIds = revisions.Select(x => x.Id).ToList(); var coverage = await db.TestCoverage.AsNoTracking().Where(x => revisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
    var executions = await db.TestExecutions.AsNoTracking().Where(x => revisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
    return Results.Ok(items.Select(x => { var latest = revisions.Where(r => r.ProcedureId == x.Id).OrderByDescending(r => r.Revision).FirstOrDefault(); var lastRun = latest is null ? null : executions.Where(e => e.ProcedureRevisionId == latest.Id).OrderByDescending(e => e.ExecutedAt).ThenByDescending(e => e.RecordedAt).FirstOrDefault();
        return new { x.Id, displayNumber = latest is null ? x.BaseNumber : x.BaseNumber + "." + latest.Revision.ToString("D2"), x.Title, x.OwnerId, level = x.Level.ToString(),
            revisionId = latest?.Id, revision = latest?.Revision, state = latest?.State.ToString(), objective = latest?.Objective,
            requirementCount = latest is null ? 0 : coverage.Count(c => c.ProcedureRevisionId == latest.Id), lastOutcome = lastRun?.Outcome.ToString(), lastExecutedAt = lastRun?.ExecutedAt }; }));
});

app.MapPost("/api/test-procedures", async (CreateTestProcedureRequest request, AeroLinkDbContext db, CancellationToken ct) =>
{
    if (await db.TestProcedures.AnyAsync(x => x.ProjectId == request.ProjectId && x.BaseNumber == request.BaseNumber.Trim().ToUpper(), ct)) return Results.Conflict(new { error = "That test procedure identifier already exists." });
    var requirementIds = request.RequirementRevisionIds.Distinct().ToList();
    if (await db.RequirementRevisions.CountAsync(x => requirementIds.Contains(x.Id), ct) != requirementIds.Count) return Results.BadRequest(new { error = "Every coverage link must reference an authoritative requirement revision." });
    try { var procedure = new TestProcedure(request.ProjectId, request.BaseNumber, request.Title, request.OwnerId, DateTimeOffset.UtcNow, request.Level);
        var revision = new TestProcedureRevision(procedure.Id, 0, request.Objective, request.Preconditions, request.Steps, request.ExpectedResult, TestProcedureState.Approved, request.OwnerId, DateTimeOffset.UtcNow);
        db.AddRange(procedure, revision); db.TestCoverage.AddRange(requirementIds.Select(id => new TestRequirementCoverage(revision.Id, id))); await db.SaveChangesAsync(ct);
        return Results.Created($"/api/test-procedures/{procedure.Id}", new { procedure.Id, revisionId = revision.Id, displayNumber = procedure.BaseNumber + ".00" }); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/test-executions", async (RecordTestExecutionRequest request, AeroLinkDbContext db, CancellationToken ct) =>
{
    var revision = await db.TestProcedureRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ProcedureRevisionId, ct); if (revision is null) return Results.NotFound();
    if (revision.State != TestProcedureState.Approved) return Results.BadRequest(new { error = "Only an approved test procedure revision can be executed." });
    var procedure = await db.TestProcedures.AsNoTracking().SingleAsync(x => x.Id == revision.ProcedureId, ct); if (procedure.ProjectId != request.ProjectId) return Results.BadRequest(new { error = "The test procedure belongs to a different project." });
    if (request.SoftwareBuildId is not null && !await db.SoftwareBuilds.AnyAsync(x => x.Id == request.SoftwareBuildId && x.ProjectId == request.ProjectId, ct)) return Results.BadRequest(new { error = "The software build belongs to a different project." });
    if (request.RetestOfExecutionId is not null && !await db.TestExecutions.AnyAsync(x => x.Id == request.RetestOfExecutionId && x.ProcedureRevisionId == request.ProcedureRevisionId, ct)) return Results.BadRequest(new { error = "A retest must reference an earlier execution of the same procedure revision." });
    try { var execution = new TestExecution(request.ProjectId, request.ProcedureRevisionId, request.SoftwareBuildId, request.RetestOfExecutionId,
        request.Outcome, request.ExecutedBy, request.Configuration, request.Determination, request.EvidenceReference, request.ExecutedAt, DateTimeOffset.UtcNow);
        db.TestExecutions.Add(execution); await db.SaveChangesAsync(ct); return Results.Created($"/api/test-executions/{execution.Id}", new { execution.Id, outcome = execution.Outcome.ToString() }); }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/test-executions", async (Guid projectId, Guid? buildId, AeroLinkDbContext db, CancellationToken ct) =>
{
    var source = db.TestExecutions.AsNoTracking().Where(x => x.ProjectId == projectId && (buildId == null || x.SoftwareBuildId == buildId));
    var rowsQuery = from execution in source join revision in db.TestProcedureRevisions.AsNoTracking() on execution.ProcedureRevisionId equals revision.Id
                      join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                      select new { execution.Id, procedureRevisionId = revision.Id, displayNumber = procedure.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                          procedure.Title, outcome = execution.Outcome.ToString(), execution.ExecutedBy, execution.Configuration, execution.Determination,
                          execution.EvidenceReference, execution.ExecutedAt, execution.RecordedAt, execution.SoftwareBuildId, execution.RetestOfExecutionId };
    var rows = await (db.Database.IsSqlite() ? rowsQuery.OrderByDescending(x => x.Id) : rowsQuery.OrderByDescending(x => x.ExecutedAt)).ToListAsync(ct); var rowIds = rows.Select(x => x.Id).ToList();
    var evidence = await (from link in db.TestExecutionEvidence.AsNoTracking().Where(x => rowIds.Contains(x.TestExecutionId)) join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id select new { link.TestExecutionId, item.Id, item.OriginalFileName, item.Size, item.Sha256, item.UploadedAt }).ToListAsync(ct);
    return Results.Ok(rows.Select(x => new { x.Id, x.procedureRevisionId, x.displayNumber, x.Title, x.outcome, x.ExecutedBy, x.Configuration, x.Determination, x.EvidenceReference, x.ExecutedAt, x.RecordedAt, x.SoftwareBuildId, x.RetestOfExecutionId, evidence = evidence.Where(e => e.TestExecutionId == x.Id).Select(e => new { e.Id, e.OriginalFileName, e.Size, e.Sha256, e.UploadedAt }) }));
});

app.MapGet("/api/verification-coverage", async (Guid projectId, Guid? baselineId, Guid? buildId, AeroLinkDbContext db, CancellationToken ct) =>
{
    if (buildId is not null) baselineId = await db.SoftwareBuilds.Where(x => x.Id == buildId && x.ProjectId == projectId).Select(x => (Guid?)x.BaselineId).SingleOrDefaultAsync(ct);
    if (baselineId is null) return Results.BadRequest(new { error = "Select a materialized baseline or software build." });
    var requirements = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                              join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                              join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                              orderby artifact.BaseNumber select new { artifact.Id, revisionId = revision.Id, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, revision.Statement }).ToListAsync(ct);
    var requirementIds = requirements.Select(x => x.revisionId).ToList(); var links = await db.TestCoverage.AsNoTracking().Where(x => requirementIds.Contains(x.RequirementRevisionId)).ToListAsync(ct);
    var procedureRevisionIds = links.Select(x => x.ProcedureRevisionId).Distinct().ToList(); var revisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.Id)).ToListAsync(ct);
    var procedures = await db.TestProcedures.AsNoTracking().Where(x => revisions.Select(r => r.ProcedureId).Contains(x.Id)).ToListAsync(ct);
    var executions = await db.TestExecutions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId) && (buildId == null || x.SoftwareBuildId == buildId)).ToListAsync(ct);
    var items = requirements.Select(req => { var coveredBy = links.Where(x => x.RequirementRevisionId == req.revisionId).Select(link => { var rev = revisions.Single(r => r.Id == link.ProcedureRevisionId); var proc = procedures.Single(p => p.Id == rev.ProcedureId); var latest = executions.Where(e => e.ProcedureRevisionId == rev.Id).OrderByDescending(e => e.ExecutedAt).ThenByDescending(e => e.RecordedAt).FirstOrDefault(); return new { revisionId = rev.Id, displayNumber = proc.BaseNumber + "." + rev.Revision.ToString("D2"), proc.Title, latestOutcome = latest?.Outcome.ToString(), latestExecutionId = latest?.Id }; }).ToList(); return new { req.Id, req.revisionId, req.displayNumber, req.Statement, covered = coveredBy.Count > 0, verified = coveredBy.Any(x => x.latestOutcome == "Pass"), coveredBy }; }).ToList();
    return Results.Ok(new { baselineId, buildId, total = items.Count, covered = items.Count(x => x.covered), verified = items.Count(x => x.verified), uncovered = items.Count(x => !x.covered), items });
});

app.Run();

public partial class Program { }

record CreateScrRequest(string BaseNumber, Guid ProjectId, Guid TargetReleaseId, string Title, string Problem, string Analysis, string Solution, string AuthorId, ChangeRequestType Type = ChangeRequestType.System);
record DraftRequirementRequest(string BaseNumber, int Revision, RequirementLevel Level, RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod);
record CreateScrDraftRequest(string BaseNumber, Guid ProjectId, Guid TargetReleaseId, string Title, string Problem, string Analysis, string Solution, string AuthorId, List<DraftRequirementRequest> RequirementChanges, ChangeRequestType Type = ChangeRequestType.System);
record UpdateScrDraftRequest(long ExpectedVersion, string ActorId, string Title, string Problem, string Analysis, string Solution, List<DraftRequirementRequest> RequirementChanges);
record CreateWorkspaceRequest(string ProgramName, string ProgramCode, string ProjectName, string SoftwareProduct, string InitialRelease, bool InitialReleaseIsReleased);
record RequirementChangeRequest(string ActorId, string BaseNumber, int Revision, RequirementLevel Level, RequirementChangeKind Kind, string Statement, string Rationale, string VerificationMethod);
record ApproverRequest(string UserId, string Name);
record SubmitReviewRequest(string ActorId, long? ExpectedVersion, List<ApproverRequest> Approvers);
record ActorRequest(string ActorId, long? ExpectedVersion);
record RequestChangesRequest(string ActorId, long? ExpectedVersion, string Reason);
record CreateBaselineRequest(string BaseNumber, int Revision, Guid ProjectId, Guid ReleaseId, Guid? PredecessorBaselineId, string Name, string ActorId);
record BaselineSelectionRequest(Guid ScrId, string ActorId);
record BaselineActorRequest(string ActorId);
record CreateBuildRequest(Guid ProjectId, Guid ReleaseId, Guid BaselineId, string BuildNumber, string Description, string RecordedBy);
record CreateTestProcedureRequest(Guid ProjectId, string BaseNumber, string Title, string OwnerId, string Objective, string Preconditions, string Steps, string ExpectedResult, List<Guid> RequirementRevisionIds, TestProcedureLevel Level = TestProcedureLevel.HighLevel);
record RecordTestExecutionRequest(Guid ProjectId, Guid ProcedureRevisionId, Guid? SoftwareBuildId, Guid? RetestOfExecutionId, TestOutcome Outcome, string ExecutedBy, string Configuration, string Determination, string EvidenceReference, DateTimeOffset ExecutedAt);
record DispositionImpactRequest(ImpactDispositionState State, string Rationale, string ActorId);
record SelectBuildRequest(Guid SoftwareBuildId, string ActorId);
record CampaignActorRequest(string ActorId);
record StartReleaseReviewRequest(string ActorId, List<ApproverRequest> Approvers);
record CompleteReleaseRequest(string ActorId);
record CreateTraceLinkRequest(Guid ProjectId, Guid SourceRevisionId, Guid TargetRevisionId, RequirementTraceType Type, string Rationale);
record GenerateDocumentsRequest(string ActorId);

static class ApiMap
{
    public static object Workspace(ProgramRecord program, ProjectRecord project, SoftwareRelease release) => new
    {
        program = new { program.Id, program.Name, program.Code },
        project = new { project.Id, project.Name, project.SoftwareProduct },
        release = new { release.Id, release.Version, release.IsReleased }
    };
    public static object ScrSummary(ScrListItem x) => new { x.Id, displayNumber = $"{x.BaseNumber}.{x.Revision:D2}", x.Title, state = x.State.ToString(), type = x.Type.ToString(), x.AuthorId, x.TargetReleaseId, x.RequirementCount, x.UpdatedAt };
    public static object ScrDetail(SystemChangeRequest x) => new
    {
        x.Id, x.BaseNumber, x.Revision, x.DisplayNumber, x.ProjectId, x.TargetReleaseId, type = x.Type.ToString(), x.Title, x.Problem, x.Analysis, x.Solution, x.AuthorId, x.Version,
        state = x.State.ToString(), x.CreatedAt, x.UpdatedAt,
        requirementChanges = x.RequirementChanges.Select(r => new { r.Id, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement, r.Rationale, r.VerificationMethod }),
        reviewCycles = x.ReviewCycles.OrderBy(c => c.Sequence).Select(c => new { c.Id, c.Sequence, state = c.State.ToString(), c.SnapshotHash, c.StartedAt, c.CompletedAt, c.ClosureReason, steps = c.Steps.OrderBy(s => s.Position).Select(s => new { s.Position, s.ApproverId, s.ApproverName, state = s.State.ToString(), s.DecidedAt }) }),
        audit = x.AuditEvents.OrderByDescending(a => a.OccurredAt).Select(a => new { a.EventType, a.ActorId, a.Detail, a.OccurredAt })
    };
    public static object Baseline(CandidateBaseline x) => new { x.Id, x.DisplayNumber, x.Name, x.ProjectId, x.ReleaseId, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt, selectionCount = x.Selections.Count };
    public static object BaselineDetail(CandidateBaseline x, IReadOnlyList<SystemChangeRequest> selected) => new
    {
        x.Id, x.DisplayNumber, x.Name, x.ProjectId, x.ReleaseId, state = x.State.ToString(), x.ContentHash, x.RequirementsHash, x.RequirementsMaterializedAt, x.CreatedAt, x.FrozenAt,
        selections = selected.OrderBy(scr => scr.DisplayNumber).Select(scr => new
        {
            scr.Id, scr.DisplayNumber, scr.Title,
            requirementChanges = scr.RequirementChanges.OrderBy(r => r.DisplayNumber).Select(r => new { r.Id, r.DisplayNumber, level = r.Level.ToString(), kind = r.Kind.ToString(), r.Statement, r.VerificationMethod })
        }),
        events = x.Events.OrderByDescending(e => e.OccurredAt).Select(e => new { e.EventType, e.ActorId, e.Detail, e.OccurredAt })
    };
}
