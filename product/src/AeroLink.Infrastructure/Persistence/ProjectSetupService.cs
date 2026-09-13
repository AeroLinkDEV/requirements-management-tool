using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Integrations;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>The persisted answers accepted by the resumable project-creation client.</summary>
public sealed record ProjectSetupUpdateCommand(
    long ExpectedVersion,
    ProjectSetupStep CurrentStep,
    string? ProjectName,
    string? SoftwareProduct,
    ProjectSetupStartKind? StartKind,
    Guid? SourceBaselineId,
    Guid? SourceImportId,
    string? InitialReleaseVersion,
    string? SelectedCategoriesJson,
    string? LadderJson,
    string? ReviewRulesJson,
    bool? ReviewRulesAccepted,
    string? RepositoryJson,
    string? MappingJson);

public sealed record ProjectSetupFinalizationResult(
    Guid ProgramId,
    Guid ProjectId,
    Guid ReleaseId,
    string RawVersion,
    string OfficialBuildName,
    bool AlreadyCompleted,
    string ResultJson);

/// <summary>
/// Durable orchestration for Create New Project. This is intentionally a small aggregate service, rather than a
/// general workflow engine: all answers live on ProjectSetupDraft and the only multi-aggregate transaction is the
/// short finalization unit that claims the draft, creates the empty project, and records its outcome.
/// </summary>
public sealed class ProjectSetupService(
    AeroLinkDbContext db,
    ProjectLadderAuthoringService ladderAuthoring,
    TestProcedureDocumentBootstrap procedureDocuments)
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ProjectSetupDraft> CreateAsync(AuthenticatedUser actor, string? initialProjectName,
        CancellationToken ct)
    {
        RequireAdministrator(actor);
        var draft = new ProjectSetupDraft(actor.Id, actor.UserName, initialProjectName);
        db.ProjectSetupDrafts.Add(draft);
        await db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task<IReadOnlyList<ProjectSetupDraft>> ListAsync(AuthenticatedUser actor, CancellationToken ct)
    {
        RequireAuthenticated(actor);
        var query = db.ProjectSetupDrafts.AsNoTracking().AsQueryable();
        if (!actor.IsAdministrator) query = query.Where(x => x.CreatorUserId == actor.Id);
        return await query.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
    }

    public async Task<ProjectSetupDraft?> ReadAsync(Guid draftId, AuthenticatedUser actor, CancellationToken ct)
    {
        RequireAuthenticated(actor);
        var draft = await db.ProjectSetupDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == draftId, ct);
        if (draft is null || !CanManage(draft, actor)) return null;
        return draft;
    }

    public async Task<ProjectSetupDraft> UpdateAsync(Guid draftId, AuthenticatedUser actor,
        ProjectSetupUpdateCommand command, CancellationToken ct)
    {
        RequireAuthenticated(actor);
        var draft = await db.ProjectSetupDrafts.SingleOrDefaultAsync(x => x.Id == draftId, ct)
            ?? throw new ProjectSetupNotFoundException();
        RequireManage(draft, actor);
        ValidateUpdatePayload(command);
        try
        {
            draft.UpdateAnswers(command.ExpectedVersion, command.CurrentStep, command.ProjectName,
                command.SoftwareProduct, command.StartKind, command.SourceBaselineId, command.SourceImportId,
                command.InitialReleaseVersion, command.SelectedCategoriesJson, command.LadderJson,
                command.ReviewRulesJson, command.ReviewRulesAccepted, command.RepositoryJson,
                command.MappingJson, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            return draft;
        }
        catch (ProjectSetupConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<ProjectSetupFinalizationResult> FinalizeAsync(Guid draftId, AuthenticatedUser actor,
        long expectedVersion, string operationKey, CancellationToken ct)
    {
        RequireAuthenticated(actor);
        if (string.IsNullOrWhiteSpace(operationKey))
            throw new ProjectSetupInvalidException("Finalization requires an idempotency key.");

        // SAVE_BOUNDARY requires pre-save reads that participate in a shared unit to be inside that unit. The
        // serializable transaction also ensures that a concurrent finalizer observes the committed outcome after
        // the first request releases the draft row lock.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var draft = await db.ProjectSetupDrafts.SingleOrDefaultAsync(x => x.Id == draftId, ct)
            ?? throw new ProjectSetupNotFoundException();
        RequireManage(draft, actor);

        if (draft.State == ProjectSetupState.Completed)
            return CompletedResult(draft);
        if (draft.State == ProjectSetupState.Finalizing)
            throw new ProjectSetupConflictException("This setup is already being finalized. Retry after it completes.");

        ValidateFreshAnswers(draft);
        try
        {
            if (!draft.BeginFinalization(expectedVersion, operationKey, DateTimeOffset.UtcNow))
                return CompletedResult(draft);
            // Claim first while keeping the transaction open. Another request cannot pass its read until this
            // transaction commits or rolls back, so duplicate submissions cannot create a second project.
            await db.SaveChangesAsync(ct);

            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord(draft.InternalProgramId, draft.InternalProgramName, draft.InternalProgramCode);
            var project = new ProjectRecord(draft.ProjectId, program.Id, draft.ProjectName, draft.SoftwareProduct);
            var parsedRelease = SoftwareBuildIdentifier.Parse(draft.InitialReleaseVersion);
            var release = new SoftwareRelease(draft.InitialReleaseId, project.Id, draft.InitialReleaseVersion,
                isReleased: false);
            if (!string.Equals(release.CanonicalIdentity, parsedRelease.OfficialName, StringComparison.Ordinal))
                throw new ProjectSetupInvalidException("The release identity could not be canonically validated.");

            var ladder = CreateLadder(draft, project.Id, now);
            var activation = ladderAuthoring.PrepareActivationForCreation(ladder, actor.UserName, now);
            db.AddRange(program, project, release, ladder);
            db.ProjectLadderConfigurationHistories.Add(new ProjectLadderConfigurationHistory(
                ladder.Id, project.Id, ladder.Version, actor.UserName, now,
                "Activated the accepted ladder before first project content.", activation.CanonicalSnapshot,
                ProjectLadderSnapshot.Hash(activation.CanonicalSnapshot), activation.SnapshotSchemaVersion));

            // The creator is the only project member at inception. Directory identities are intentionally not
            // copied from the FMS showcase or any other program; staffing can be configured after completion.
            // An administrator may resume somebody else's draft, but the draft creator remains the legitimate
            // project manager at completion. The actual finalizer stays attributable in GrantedBy and audit.
            db.ProgramMemberships.Add(new ProgramMembership(draft.CreatorUserId, program.Id, ProgramRole.Administrator,
                actor.UserName, now));
            db.ProjectVerificationVocabularies.Add(ProjectVerificationVocabulary.Founding(project.Id, now));
            db.ProjectRepositoryConfigurations.Add(CreateRepositoryConfiguration(project.Id, draft, actor.UserName, now));
            AddReviewRules(project.Id, ladder, draft.ReviewRulesJson, actor.UserName, now);

            // This sees the tracked Active ladder through the local aggregate and creates only the empty
            // procedure containers required by the selected profile. It cannot invent engineering content.
            await procedureDocuments.EnsureForProjectAsync(project.Id, ct);

            var resultJson = JsonSerializer.Serialize(new
            {
                programId = program.Id,
                projectId = project.Id,
                releaseId = release.Id,
                version = release.Version,
                officialBuildName = release.CanonicalIdentity,
                state = "InWork",
            }, WireJson);
            draft.Complete(resultJson, program.Id, project.Id, release.Id, DateTimeOffset.UtcNow);
            db.SecurityAuditEvents.Add(new SecurityAuditEvent("ProjectSetupCompleted", actor.UserName,
                draft.Id.ToString("D"), "Success",
                $"Created Project {project.Id:D} from a fresh setup draft; no engineering content was inherited.",
                "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(program.Id, project.Id, release.Id, release.Version, release.CanonicalIdentity!, false,
                resultJson);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ProjectSetupConflictException("Another setup or project creation request won this draft.", ex);
        }
        catch (DbUpdateException ex) when (IsUniqueConflict(ex) || IsRetryableRace(ex))
        {
            throw new ProjectSetupConflictException("A project identity or controlled release identity already exists.", ex);
        }
    }

    private static void ValidateFreshAnswers(ProjectSetupDraft draft)
    {
        if (draft.StartKind != ProjectSetupStartKind.Fresh)
            throw new ProjectSetupInvalidException(
                "This backend foundation finalizes Fresh starts only; select an existing AeroLink or external source for the staged import path.");
        if (draft.SourceBaselineId is not null || draft.SourceImportId is not null)
            throw new ProjectSetupInvalidException("A Fresh start cannot carry a source baseline or import.");
        if (string.IsNullOrWhiteSpace(draft.ProjectName) || string.IsNullOrWhiteSpace(draft.SoftwareProduct))
            throw new ProjectSetupInvalidException("Project name and software product are required before finalization.");
        _ = SoftwareBuildIdentifier.Parse(draft.InitialReleaseVersion);
        var categories = Deserialize<string[]>(draft.SelectedCategoriesJson, "selected categories");
        if (categories.Length != 0)
            throw new ProjectSetupInvalidException("Fresh starts cannot inherit content categories.");
        _ = Deserialize<JsonElement>(draft.LadderJson, "ladder");
        _ = Deserialize<JsonElement>(draft.ReviewRulesJson, "review rules");
        _ = Deserialize<JsonElement>(draft.RepositoryJson, "repository settings");
        _ = Deserialize<JsonElement>(draft.MappingJson, "mapping");
        if (!draft.ReviewRulesAccepted)
            throw new ProjectSetupInvalidException("Review and approval rules must be explicitly accepted before finalization.");
        var acceptanceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{draft.LadderJson}\n{draft.ReviewRulesJson}"))).ToLowerInvariant();
        if (!string.Equals(draft.ReviewRulesAcceptanceHash, acceptanceHash, StringComparison.Ordinal))
            throw new ProjectSetupInvalidException("The accepted ladder and review rules changed. Review and accept them again.");
        ValidateRepository(draft.RepositoryJson);
        ValidateReviewRules(draft.ReviewRulesJson);
    }

    private static void ValidateUpdatePayload(ProjectSetupUpdateCommand command)
    {
        if (!Enum.IsDefined(command.CurrentStep))
            throw new ProjectSetupInvalidException("The setup step is not supported.");
        if (command.StartKind is null)
        {
            if (command.SourceBaselineId is not null || command.SourceImportId is not null)
                throw new ProjectSetupInvalidException("Source identities require a supported setup source.");
        }
        else
        {
            var startKind = command.StartKind.Value;
            if (!Enum.IsDefined(startKind)) throw new ProjectSetupInvalidException("The setup source is not supported.");
            switch (startKind)
            {
                case ProjectSetupStartKind.Fresh when command.SourceBaselineId is not null || command.SourceImportId is not null:
                    throw new ProjectSetupInvalidException("A Fresh start cannot carry a source identity.");
                case ProjectSetupStartKind.AeroLinkBaseline when command.SourceBaselineId is null || command.SourceImportId is not null:
                    throw new ProjectSetupInvalidException("An AeroLink baseline start requires only the exact baseline identity.");
                case ProjectSetupStartKind.ExternalBaseline when command.SourceImportId is null || command.SourceBaselineId is not null:
                    throw new ProjectSetupInvalidException("An external baseline start requires only the staged import identity.");
            }
        }
        if (command.SelectedCategoriesJson is not null)
        {
            var categories = Deserialize<string[]>(command.SelectedCategoriesJson, "selected categories");
            if (categories.Any(x => string.IsNullOrWhiteSpace(x)))
                throw new ProjectSetupInvalidException("Selected content categories must be named strings.");
        }
        if (command.LadderJson is not null)
        {
            var ladder = Deserialize<JsonElement>(command.LadderJson, "ladder");
            if (ladder.ValueKind != JsonValueKind.Object)
                throw new ProjectSetupInvalidException("The reviewed ladder must be an object.");
            if (ladder.EnumerateObject().Any() && GetPropertyOrNull(ladder, "steps") is null)
                throw new ProjectSetupInvalidException("A reviewed ladder must provide typed steps and relationships.");
        }
        if (command.ReviewRulesJson is not null)
            ValidateReviewRules(command.ReviewRulesJson);
        if (command.RepositoryJson is not null)
            ValidateRepository(command.RepositoryJson);
        if (command.MappingJson is not null)
        {
            var mapping = Deserialize<JsonElement>(command.MappingJson, "mapping");
            if (mapping.ValueKind != JsonValueKind.Object)
                throw new ProjectSetupInvalidException("Mapping answers must be a typed object.");
        }
    }

    private static void ValidateReviewRules(string json)
    {
        var rules = Deserialize<JsonElement>(json, "review rules");
        if (rules.ValueKind != JsonValueKind.Object)
            throw new ProjectSetupInvalidException("Review rules must be a typed object.");
        // An empty object means the product standard will be offered for explicit creator acceptance. Any
        // non-empty proposal must use the typed rules array; arbitrary browser JSON can never satisfy the gate.
        if (rules.EnumerateObject().Any() && GetPropertyOrNull(rules, "rules") is not { ValueKind: JsonValueKind.Array })
            throw new ProjectSetupInvalidException("Review rules must contain a typed 'rules' array or be empty for the standard.");
        if (GetPropertyOrNull(rules, "rules") is { } list)
        {
            foreach (var rule in list.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object
                    || GetPropertyOrNull(rule, "subject") is not { ValueKind: JsonValueKind.String }
                    || GetPropertyOrNull(rule, "stages") is not { ValueKind: JsonValueKind.Array })
                    throw new ProjectSetupInvalidException("Each review rule requires a subject and typed stages.");
                if (!rule.GetProperty("stages").EnumerateArray().Any())
                    throw new ProjectSetupInvalidException("Each review rule requires at least one stage.");
            }
            _ = Deserialize<ReviewRulesWire>(json, "review rules");
        }
    }

    private static void ValidateRepository(string json)
    {
        var repository = Deserialize<JsonElement>(json, "repository settings");
        if (repository.ValueKind != JsonValueKind.Object)
            throw new ProjectSetupInvalidException("Repository setup must be a typed object.");
        var mode = GetPropertyOrNull(repository, "mode");
        if (mode is not { ValueKind: JsonValueKind.String }
            || !mode.Value.GetString()!.Equals("ConnectNow", StringComparison.OrdinalIgnoreCase)
                && !mode.Value.GetString()!.Equals("ConfigureLater", StringComparison.OrdinalIgnoreCase))
            throw new ProjectSetupInvalidException("Repository setup mode must be ConnectNow or ConfigureLater.");
        var connectNow = mode.Value.GetString()!.Equals("ConnectNow", StringComparison.OrdinalIgnoreCase);
        var endpoint = GetPropertyOrNull(repository, "endpoint");
        if (endpoint is { ValueKind: not (JsonValueKind.String or JsonValueKind.Null) })
            throw new ProjectSetupInvalidException("Repository endpoint must be a string or null.");
        var endpointText = endpoint is { ValueKind: JsonValueKind.String } ? endpoint.Value.GetString() : null;
        if (connectNow)
        {
            if (!Uri.TryCreate(endpointText?.Trim(), UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ProjectSetupInvalidException("A connected repository requires an HTTPS endpoint without credentials, query, or fragment.");
        }
        else if (!string.IsNullOrWhiteSpace(endpointText))
            throw new ProjectSetupInvalidException("A deferred repository cannot carry an endpoint.");
        if (GetPropertyOrNull(repository, "status") is { } status)
        {
            var statusText = status.ValueKind == JsonValueKind.String ? status.GetString() : null;
            var isConfiguredUnverified = string.Equals(statusText, "Configured-unverified", StringComparison.OrdinalIgnoreCase)
                || string.Equals(statusText, "ConfiguredUnverified", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(statusText, "Pending", StringComparison.OrdinalIgnoreCase) && !isConfiguredUnverified)
                throw new ProjectSetupInvalidException("Repository setup status is derived by the server; Verified cannot be supplied by the browser.");
            if (!connectNow && isConfiguredUnverified)
                throw new ProjectSetupInvalidException("A deferred repository remains Pending until it is configured.");
        }
    }

    private ProjectLadderConfiguration CreateLadder(ProjectSetupDraft draft, Guid projectId, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(draft.LadderJson);
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.EnumerateObject().Any() == false)
            return NewProjectLadderFactory.Create(projectId, now);

        var definition = Deserialize<LadderDefinitionWire>(draft.LadderJson, "ladder");
        if (definition.Steps is null || definition.Steps.Count == 0)
            throw new ProjectSetupInvalidException("The reviewed ladder must contain at least one supported level.");
        IReadOnlyList<LadderStepDraft> steps;
        IReadOnlyList<LadderRelationshipDraft> relationships;
        try
        {
            (steps, relationships) = ProjectLadderDraftValidator.Validate(
                definition.Steps.Select(x => new LadderStepDraft(x.CatalogueEntry, x.Position,
                    x.Capabilities, x.EnabledArtifactKinds)).ToArray(),
                (definition.Relationships ?? []).Select(x => new LadderRelationshipDraft(x.Parent, x.Child)).ToArray(),
                LegacyLadderPolicy.Instance);
        }
        catch (DomainException ex) { throw new ProjectSetupInvalidException(ex.Message, ex); }

        var ladder = ProjectLadderConfiguration.CreateDraft(projectId, now);
        var byName = new Dictionary<string, ProjectLadderStep>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var entity = new ProjectLadderStep(ladder.Id, projectId,
                Enum.Parse<RequirementLevel>(step.CatalogueEntry, false), step.Position, step.Capabilities,
                now, step.EnabledArtifactKinds);
            ladder.Steps.Add(entity);
            byName.Add(step.CatalogueEntry, entity);
        }
        foreach (var relationship in relationships)
            ladder.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(ladder.Id, projectId,
                byName[relationship.Parent].Id, byName[relationship.Child].Id, now));
        return ladder;
    }

    private void AddReviewRules(Guid projectId, ProjectLadderConfiguration ladder, string rulesJson, string actor,
        DateTimeOffset now)
    {
        var rules = new List<(ReviewSubject Subject, string Name, ProgramRole ReviewRole)>();
        using var parsed = JsonDocument.Parse(rulesJson);
        if (parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.EnumerateObject().Any())
        {
            var supplied = Deserialize<ReviewRulesWire>(rulesJson, "review rules");
            if (supplied.Rules is null || supplied.Rules.Count == 0)
                throw new ProjectSetupInvalidException("The reviewed rules must contain at least one typed rule.");
            var applicable = ApplicableSubjects(ladder);
            var suppliedSubjects = supplied.Rules.Select(x => x.Subject).ToArray();
            if (suppliedSubjects.Distinct().Count() != suppliedSubjects.Length || !applicable.SetEquals(suppliedSubjects))
                throw new ProjectSetupInvalidException("Review rules must cover each applicable ladder subject exactly once.");
            foreach (var suppliedRule in supplied.Rules)
            {
                if (suppliedRule.Stages is null || suppliedRule.Stages.Count == 0)
                    throw new ProjectSetupInvalidException($"Review rule {suppliedRule.Subject} requires a stage.");
                if (!suppliedRule.Stages.Any(x => x.Kind == ReviewStageKind.Review)
                    || !suppliedRule.Stages.Any(x => x.Kind == ReviewStageKind.Approval))
                    throw new ProjectSetupInvalidException($"Review rule {suppliedRule.Subject} requires explicit Review and Approval stages.");
                var workflow = new ReviewWorkflow(projectId, suppliedRule.Name ?? suppliedRule.Subject.ToString(),
                    suppliedRule.Subject, ReviewMode.Sequential,
                    suppliedRule.Stages.Select(x => new ReviewWorkflowStageDraft(x.Name, x.RequiredRole, x.Kind,
                        x.AuthorityKind)).ToArray(), actor, now);
                workflow.Activate(actor, now);
                db.ReviewWorkflows.Add(workflow);
            }
            return;
        }

        var levels = ladder.Steps.Select(x => Enum.Parse<RequirementLevel>(x.CatalogueEntry, false)).ToHashSet();
        if (levels.Contains(RequirementLevel.System)) rules.Add((ReviewSubject.System, "System requirements", ProgramRole.SystemEngineer));
        if (levels.Contains(RequirementLevel.HighLevel) || levels.Contains(RequirementLevel.LowLevel))
            rules.Add((ReviewSubject.Software, "Software requirements", ProgramRole.SoftwareEngineer));
        if (levels.Contains(RequirementLevel.Interface))
            rules.Add((ReviewSubject.Interface, "Interface requirements", ProgramRole.ConfigurationManager));
        if (levels.Contains(RequirementLevel.System) && HasArtifact(ladder, RequirementLevel.System, VerificationArtifactKind.Procedure))
            rules.Add((ReviewSubject.SystemTest, "System test procedures", ProgramRole.SystemTestEngineer));
        if (levels.Contains(RequirementLevel.HighLevel))
        {
            if (HasArtifact(ladder, RequirementLevel.HighLevel, VerificationArtifactKind.Case))
                rules.Add((ReviewSubject.HighLevelSoftwareCase, "High-level software test cases", ProgramRole.SoftwareTestEngineer));
            if (HasArtifact(ladder, RequirementLevel.HighLevel, VerificationArtifactKind.Procedure))
                rules.Add((ReviewSubject.HighLevelSoftwareProcedure, "High-level software test procedures", ProgramRole.SoftwareTestEngineer));
        }
        if (levels.Contains(RequirementLevel.LowLevel))
        {
            if (HasArtifact(ladder, RequirementLevel.LowLevel, VerificationArtifactKind.Case))
                rules.Add((ReviewSubject.LowLevelSoftwareCase, "Low-level software test cases", ProgramRole.SoftwareTestEngineer));
            if (HasArtifact(ladder, RequirementLevel.LowLevel, VerificationArtifactKind.Procedure))
                rules.Add((ReviewSubject.LowLevelSoftwareProcedure, "Low-level software test procedures", ProgramRole.SoftwareTestEngineer));
        }
        foreach (var (subject, name, reviewRole) in rules)
        {
            var workflow = new ReviewWorkflow(projectId, name, subject, ReviewMode.Sequential,
            [
                new ReviewWorkflowStageDraft("Engineering review", reviewRole, ReviewStageKind.Review,
                    ReviewStageAuthorityKind.BaseRole),
                new ReviewWorkflowStageDraft("Project acceptance", ProgramRole.ProjectEngineer,
                    ReviewStageKind.Approval, ReviewStageAuthorityKind.LeadershipPosition),
            ], actor, now);
            workflow.Activate(actor, now);
            db.ReviewWorkflows.Add(workflow);
        }
    }

    private static HashSet<ReviewSubject> ApplicableSubjects(ProjectLadderConfiguration ladder)
    {
        var subjects = new HashSet<ReviewSubject>();
        var levels = ladder.Steps.Select(x => Enum.Parse<RequirementLevel>(x.CatalogueEntry, false)).ToHashSet();
        if (levels.Contains(RequirementLevel.System)) subjects.Add(ReviewSubject.System);
        if (levels.Contains(RequirementLevel.HighLevel) || levels.Contains(RequirementLevel.LowLevel)) subjects.Add(ReviewSubject.Software);
        if (levels.Contains(RequirementLevel.Interface)) subjects.Add(ReviewSubject.Interface);
        if (levels.Contains(RequirementLevel.System) && HasArtifact(ladder, RequirementLevel.System, VerificationArtifactKind.Procedure)) subjects.Add(ReviewSubject.SystemTest);
        if (levels.Contains(RequirementLevel.HighLevel))
        {
            if (HasArtifact(ladder, RequirementLevel.HighLevel, VerificationArtifactKind.Case)) subjects.Add(ReviewSubject.HighLevelSoftwareCase);
            if (HasArtifact(ladder, RequirementLevel.HighLevel, VerificationArtifactKind.Procedure)) subjects.Add(ReviewSubject.HighLevelSoftwareProcedure);
        }
        if (levels.Contains(RequirementLevel.LowLevel))
        {
            if (HasArtifact(ladder, RequirementLevel.LowLevel, VerificationArtifactKind.Case)) subjects.Add(ReviewSubject.LowLevelSoftwareCase);
            if (HasArtifact(ladder, RequirementLevel.LowLevel, VerificationArtifactKind.Procedure)) subjects.Add(ReviewSubject.LowLevelSoftwareProcedure);
        }
        return subjects;
    }

    private static ProjectRepositoryConfiguration CreateRepositoryConfiguration(Guid projectId,
        ProjectSetupDraft draft, string actor, DateTimeOffset now)
    {
        var repository = Deserialize<RepositoryWire>(draft.RepositoryJson, "repository settings");
        if (string.IsNullOrWhiteSpace(repository.Mode))
            throw new ProjectSetupInvalidException("Repository setup mode is required.");
        if (!Enum.TryParse<ProjectRepositorySetupMode>(repository.Mode, true, out var mode))
            throw new ProjectSetupInvalidException("Repository setup mode must be ConnectNow or ConfigureLater.");
        // The entity derives status. Browser-provided Verified is never accepted as server evidence.
        return new ProjectRepositoryConfiguration(projectId, mode, repository.Provider,
            repository.Endpoint, actor, now);
    }

    private static bool HasArtifact(ProjectLadderConfiguration ladder, RequirementLevel level,
        VerificationArtifactKind kind) => ladder.Steps.Any(step =>
            Enum.Parse<RequirementLevel>(step.CatalogueEntry, false) == level
            && step.EnabledArtifactKinds.Contains(kind));

    private static ProjectSetupFinalizationResult CompletedResult(ProjectSetupDraft draft)
    {
        if (draft.CompletedProgramId is null || draft.CompletedProjectId is null || draft.CompletedReleaseId is null
            || string.IsNullOrWhiteSpace(draft.FinalizationResultJson))
            throw new ProjectSetupInvalidException("The completed setup has no recoverable finalization outcome.");
        var result = Deserialize<CompletedWire>(draft.FinalizationResultJson, "finalization result");
        return new(draft.CompletedProgramId.Value, draft.CompletedProjectId.Value, draft.CompletedReleaseId.Value,
            result.Version ?? draft.InitialReleaseVersion, result.OfficialBuildName ?? draft.InitialReleaseCanonicalIdentity,
            true, draft.FinalizationResultJson);
    }

    private static T Deserialize<T>(string json, string field)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, WireJson)
                ?? throw new ProjectSetupInvalidException($"The {field} payload is empty.");
        }
        catch (JsonException ex) { throw new ProjectSetupInvalidException($"The {field} payload is invalid JSON.", ex); }
    }

    private static JsonElement? GetPropertyOrNull(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static bool CanManage(ProjectSetupDraft draft, AuthenticatedUser actor) =>
        actor.IsAdministrator || draft.CreatorUserId == actor.Id;

    private static void RequireManage(ProjectSetupDraft draft, AuthenticatedUser actor)
    {
        if (!CanManage(draft, actor)) throw new ProjectSetupAccessException();
    }

    private static void RequireAuthenticated(AuthenticatedUser actor)
    {
        if (actor.Id == Guid.Empty) throw new ProjectSetupAccessException();
    }

    private static void RequireAdministrator(AuthenticatedUser actor)
    {
        RequireAuthenticated(actor);
        if (!actor.IsAdministrator) throw new ProjectSetupAccessException();
    }

    private static bool IsUniqueConflict(DbUpdateException exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetryableRace(DbUpdateException exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("40001", StringComparison.OrdinalIgnoreCase)
            || message.Contains("40P01", StringComparison.OrdinalIgnoreCase)
            || message.Contains("serialization failure", StringComparison.OrdinalIgnoreCase)
            || message.Contains("deadlock detected", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record LadderDefinitionWire(List<LadderStepWire>? Steps, List<LadderRelationshipWire>? Relationships);
    private sealed record LadderStepWire(string CatalogueEntry, int Position, LevelCapabilities Capabilities,
        List<VerificationArtifactKind>? EnabledArtifactKinds);
    private sealed record LadderRelationshipWire(string Parent, string Child);
    private sealed record ReviewRulesWire(List<ReviewRuleWire>? Rules);
    private sealed record ReviewRuleWire(ReviewSubject Subject, string? Name, List<ReviewStageWire>? Stages);
    private sealed record ReviewStageWire(string Name, ProgramRole RequiredRole, ReviewStageKind Kind,
        ReviewStageAuthorityKind? AuthorityKind);
    private sealed record RepositoryWire(string Mode, string? Provider, string? Endpoint);
    private sealed record CompletedWire(string? Version, string? OfficialBuildName);
}

public sealed class ProjectSetupNotFoundException : InvalidOperationException;
public sealed class ProjectSetupAccessException : InvalidOperationException;
public sealed class ProjectSetupConflictException : InvalidOperationException
{
    public ProjectSetupConflictException(string message) : base(message) { }
    public ProjectSetupConflictException(string message, Exception inner) : base(message, inner) { }
}
public sealed class ProjectSetupInvalidException : InvalidOperationException
{
    public ProjectSetupInvalidException(string message) : base(message) { }
    public ProjectSetupInvalidException(string message, Exception inner) : base(message, inner) { }
}
