using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Api;

/// <summary>Recoverable Create New Project draft routes. The internal Program backing scope is never in this wire shape.</summary>
public static class ProjectSetupEndpoints
{
    public static void MapProjectSetupEndpoints(this WebApplication app)
    {
        app.MapPost("/api/project-setups", async (CreateProjectSetupRequest request, HttpContext http,
            ProjectSetupService service, CancellationToken ct) =>
        {
            try
            {
                var draft = await service.CreateAsync(http.UserAccount(), request.ProjectName, ct);
                return Results.Created($"/api/project-setups/{draft.Id}", View(draft));
            }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
            catch (ProjectSetupInvalidException ex) { return Results.BadRequest(new { code = "invalid_draft", error = ex.Message }); }
            catch (DomainException ex) { return Results.BadRequest(new { code = "invalid_draft", error = ex.Message }); }
        });

        app.MapGet("/api/project-setups", async (HttpContext http, ProjectSetupService service, CancellationToken ct) =>
        {
            try
            {
                var drafts = await service.ListAsync(http.UserAccount(), ct);
                return Results.Ok(drafts.Select(ListView));
            }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
        });

        app.MapGet("/api/project-setups/{draftId:guid}", async (Guid draftId, HttpContext http,
            ProjectSetupService service, CancellationToken ct) =>
        {
            try
            {
                var draft = await service.ReadAsync(draftId, http.UserAccount(), ct);
                return draft is null ? Results.NotFound() : Results.Ok(View(draft));
            }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
        });

        app.MapPut("/api/project-setups/{draftId:guid}", async (Guid draftId, ProjectSetupUpdateRequest request,
            HttpContext http, ProjectSetupService service, CancellationToken ct) =>
        {
            try
            {
                var draft = await service.UpdateAsync(draftId, http.UserAccount(), request.ToCommand(), ct);
                return Results.Ok(View(draft));
            }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
            catch (ProjectSetupNotFoundException) { return Results.NotFound(); }
            catch (ProjectSetupConcurrencyException ex) { return Results.Conflict(new { code = "draft_conflict", error = ex.Message }); }
            catch (ProjectSetupInvalidException ex) { return Results.BadRequest(new { code = "invalid_draft", error = ex.Message }); }
            catch (DomainException ex) { return Results.BadRequest(new { code = "invalid_draft", error = ex.Message }); }
        });

        app.MapPost("/api/project-setups/{draftId:guid}/save-and-exit", async (Guid draftId,
            ProjectSetupUpdateRequest request, HttpContext http, ProjectSetupService service, CancellationToken ct) =>
        {
            try
            {
                var draft = await service.UpdateAsync(draftId, http.UserAccount(), request.ToCommand(), ct);
                return Results.Ok(new { saved = true, draft = View(draft) });
            }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
            catch (ProjectSetupNotFoundException) { return Results.NotFound(); }
            catch (ProjectSetupConcurrencyException ex) { return Results.Conflict(new { code = "draft_conflict", error = ex.Message }); }
            catch (ProjectSetupInvalidException ex) { return Results.BadRequest(new { code = "invalid_draft", error = ex.Message }); }
            catch (DomainException ex) { return Results.BadRequest(new { code = "invalid_draft", error = ex.Message }); }
        });

        app.MapPost("/api/project-setups/{draftId:guid}/finalize", async (Guid draftId,
            FinalizeProjectSetupRequest request, HttpContext http, ProjectSetupService service, CancellationToken ct) =>
        {
            try
            {
                var operationKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                    ? http.Request.Headers["Idempotency-Key"].ToString() : request.IdempotencyKey;
                var result = await service.FinalizeAsync(draftId, http.UserAccount(), request.ExpectedVersion,
                    operationKey, ct, request.Password, request.SourceAssertionHash,
                    request.SourceAssertionAccepted);
                return Results.Ok(new
                {
                    state = "Completed",
                    alreadyCompleted = result.AlreadyCompleted,
                    programId = result.ProgramId,
                    projectId = result.ProjectId,
                    releaseId = result.ReleaseId,
                    version = result.RawVersion,
                    officialBuildName = result.OfficialBuildName,
                });
            }
            catch (ProjectSetupAccessException) { return Results.Forbid(); }
            catch (ProjectSetupNotFoundException) { return Results.NotFound(); }
            catch (ProjectSetupConflictException ex) { return Results.Conflict(new { code = "finalization_conflict", error = ex.Message }); }
            catch (ProjectSetupInvalidException ex) { return Results.BadRequest(new { code = "cannot_finalize", error = ex.Message, findings = ex.Findings }); }
            catch (DomainException ex) { return Results.BadRequest(new { code = "cannot_finalize", error = ex.Message }); }
        });
    }

    private static object ListView(ProjectSetupDraft draft) => new
    {
        draftId = draft.Id,
        state = draft.State.ToString(),
        currentStep = draft.CurrentStep.ToString(),
        version = draft.Version,
        project = new { name = draft.ProjectName, softwareProduct = draft.SoftwareProduct },
        lastSavedAt = draft.LastSavedAt,
    };

    private static object View(ProjectSetupDraft draft) => new
    {
        draftId = draft.Id,
        state = draft.State.ToString(),
        currentStep = draft.CurrentStep.ToString(),
        version = draft.Version,
        createdAt = draft.CreatedAt,
        updatedAt = draft.UpdatedAt,
        lastSavedAt = draft.LastSavedAt,
        project = new { name = draft.ProjectName, softwareProduct = draft.SoftwareProduct },
        start = new { kind = draft.StartKind?.ToString(), sourceBaselineId = draft.SourceBaselineId, sourceImportId = draft.SourceImportId },
        build = new { version = draft.InitialReleaseVersion, officialName = draft.InitialReleaseCanonicalIdentity },
        selectedCategories = Parse(draft.SelectedCategoriesJson),
        ladder = Parse(draft.LadderJson),
        reviewRules = new
        {
            accepted = draft.ReviewRulesAccepted,
            acceptanceHash = draft.ReviewRulesAcceptanceHash,
            definition = Parse(draft.ReviewRulesJson),
            suggestedDefinition = SuggestedRules(draft),
        },
        // The authoritative, side-effect-free verdict for this saved configuration. Its scope is the
        // ladder/profile and review-rule compatibility only; administrator authority, unsaved local edits,
        // source reconciliation, the source signature and the final transactional gate stay separate.
        validation = ProjectSetupReadiness.Evaluate(draft),
        repository = Parse(draft.RepositoryJson),
        mapping = Parse(draft.MappingJson),
        finalization = draft.CompletedProjectId is null ? null : new { programId = draft.CompletedProgramId, projectId = draft.CompletedProjectId, releaseId = draft.CompletedReleaseId },
    };

    private static JsonElement Parse(string json)
    {
        try { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static JsonElement? SuggestedRules(ProjectSetupDraft draft)
    {
        try { return Parse(ProjectSetupReviewRules.SuggestedJson(draft.LadderJson, draft.ProjectId)); }
        catch (InvalidOperationException) { return null; }
    }
}

public sealed record CreateProjectSetupRequest(string? ProjectName = null);

public sealed class ProjectSetupUpdateRequest
{
    public long ExpectedVersion { get; set; }
    public ProjectSetupStep CurrentStep { get; set; } = ProjectSetupStep.Details;
    public ProjectSetupProjectRequest? Project { get; set; }
    public ProjectSetupStartRequest? Start { get; set; }
    public ProjectSetupBuildRequest? Build { get; set; }
    public JsonElement? SelectedCategories { get; set; }
    public JsonElement? Ladder { get; set; }
    public JsonElement? ReviewRules { get; set; }
    public bool? ReviewRulesAccepted { get; set; }
    public JsonElement? Repository { get; set; }
    public JsonElement? Mapping { get; set; }

    public ProjectSetupUpdateCommand ToCommand() => new(ExpectedVersion, CurrentStep,
        Project?.Name, Project?.SoftwareProduct, Start?.Kind, Start?.SourceBaselineId, Start?.SourceImportId,
        Build?.Version, Raw(SelectedCategories), Raw(Ladder), Raw(ReviewRules), ReviewRulesAccepted,
        Raw(Repository), Raw(Mapping));

    private static string? Raw(JsonElement? element) => element is { } value && value.ValueKind != JsonValueKind.Undefined
        ? value.GetRawText() : null;
}

public sealed record ProjectSetupProjectRequest(string? Name, string? SoftwareProduct);
public sealed record ProjectSetupStartRequest(ProjectSetupStartKind? Kind, Guid? SourceBaselineId, Guid? SourceImportId);
public sealed record ProjectSetupBuildRequest(string? Version);
public sealed record FinalizeProjectSetupRequest(long ExpectedVersion, string? IdempotencyKey = null,
    string? Password = null, string? SourceAssertionHash = null, bool SourceAssertionAccepted = false);
