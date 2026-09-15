using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using AeroLink.Domain.Baselines;
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
    TestProcedureDocumentBootstrap procedureDocuments,
    ProjectSetupInceptionService? inception = null)
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
        // A discarded setup is no longer active work: it stays in the table for attribution but is not offered
        // for resume. Completed and in-flight setups remain discoverable so their recorded result is reachable.
        var query = db.ProjectSetupDrafts.AsNoTracking().AsQueryable()
            .Where(x => x.State != ProjectSetupState.Abandoned);
        if (!actor.IsAdministrator) query = query.Where(x => x.CreatorUserId == actor.Id);
        // SQLite cannot order DateTimeOffset values server-side. Draft discovery is an intentionally small,
        // authorized list, so materialize it and apply the same stable ordering in the application.
        return (await query.ToListAsync(ct)).OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id).ToList();
    }

    /// <summary>
    /// Discards one unfinished saved setup. Authorization is the same creator-or-administrator boundary that
    /// governs editing; the state guard refuses Completed and Finalizing so abandonment cannot hide a created
    /// project or a finalization that is still in flight; and the version token plus the draft's concurrency
    /// token resolve discard against a concurrent save or finalization. Nothing is deleted: the draft row, its
    /// staged source package and any shared evidence remain, and the setup simply stops being discoverable.
    /// </summary>
    public async Task<ProjectSetupDraft> DiscardAsync(Guid draftId, AuthenticatedUser actor,
        long expectedVersion, CancellationToken ct)
    {
        RequireAuthenticated(actor);
        var draft = await db.ProjectSetupDrafts.SingleOrDefaultAsync(x => x.Id == draftId, ct)
            ?? throw new ProjectSetupNotFoundException();
        RequireManage(draft, actor);
        // A repeat request reaches the state the caller asked for, so it reports the same outcome instead of
        // turning a double-click or a retried request into an error.
        if (draft.State == ProjectSetupState.Abandoned) return draft;
        try
        {
            var now = DateTimeOffset.UtcNow;
            draft.Abandon(expectedVersion, now);
            db.SecurityAuditEvents.Add(new SecurityAuditEvent("ProjectSetupDiscarded", actor.UserName,
                draft.Id.ToString("D"), "Success",
                $"Discarded the unfinished setup '{SetupName(draft)}'; no project, build or controlled record was deleted.",
                "local", now));
            await db.SaveChangesAsync(ct);
            return draft;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            db.ChangeTracker.Clear();
            throw new ProjectSetupConflictException(
                "This setup changed after it was opened. Refresh the saved setups before discarding.", ex);
        }
    }

    private static string SetupName(ProjectSetupDraft draft) =>
        string.IsNullOrWhiteSpace(draft.ProjectName) ? "Untitled Project" : draft.ProjectName.Trim();

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
            var selectedSourcePackage = await SelectedSourcePackageAsync(draft, ct);
            ValidateSourceBoundUpdate(draft, command, selectedSourcePackage);
            // Resolve the maintained standard before the acceptance hash is calculated. Once accepted, the
            // draft retains the concrete typed definition rather than regenerating rules during finalization.
            var reviewRulesJson = command.ReviewRulesJson ?? draft.ReviewRulesJson;
            if (command.ReviewRulesAccepted == true && ProjectSetupReviewRules.IsEmptyDefinition(reviewRulesJson))
            {
                try
                {
                    reviewRulesJson = ProjectSetupReviewRules.SuggestedJson(
                        command.LadderJson ?? draft.LadderJson, draft.ProjectId);
                }
                catch (InvalidOperationException ex)
                {
                    throw new ProjectSetupInvalidException(
                        "The maintained review standard could not be derived from the selected ladder.", ex);
                }
            }

            var effectiveCommand = command with
            {
                SelectedCategoriesJson = command.StartKind == ProjectSetupStartKind.Fresh ? "[]" : command.SelectedCategoriesJson,
                MappingJson = command.StartKind == ProjectSetupStartKind.Fresh ? "{}" : command.MappingJson,
                LadderJson = command.LadderJson is not null && !JsonEquivalent(command.LadderJson, draft.LadderJson)
                    ? command.LadderJson : null,
                ReviewRulesJson = command.ReviewRulesJson is not null || command.ReviewRulesAccepted == true
                    ? (JsonEquivalent(reviewRulesJson, draft.ReviewRulesJson) ? null : reviewRulesJson) : null,
            };
            if (selectedSourcePackage is not null && effectiveCommand.LadderJson is not null)
            {
                // A source manifest is valid only against the ladder it was reconciled with. Preserve the
                // staged answers while invalidating its accepted reconciliation; the source endpoint must run
                // the server-side reconciler again before materialization.
                selectedSourcePackage.RecordConfiguration(selectedSourcePackage.SelectedCategoriesJson,
                    selectedSourcePackage.MappingJson, DateTimeOffset.UtcNow);
            }
            draft.UpdateAnswers(effectiveCommand.ExpectedVersion, effectiveCommand.CurrentStep, effectiveCommand.ProjectName,
                effectiveCommand.SoftwareProduct, effectiveCommand.StartKind, effectiveCommand.SourceBaselineId, effectiveCommand.SourceImportId,
                effectiveCommand.InitialReleaseVersion, effectiveCommand.SelectedCategoriesJson, effectiveCommand.LadderJson,
                effectiveCommand.ReviewRulesJson, effectiveCommand.ReviewRulesAccepted, effectiveCommand.RepositoryJson,
                effectiveCommand.MappingJson, DateTimeOffset.UtcNow);
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
        long expectedVersion, string operationKey, CancellationToken ct, string? password = null,
        string? sourceAssertionHash = null, bool sourceAssertionAccepted = false)
    {
        // Draft management is intentionally broader than project creation: the creator can save, read, and
        // resume their draft, while only a currently authenticated AeroLink administrator may create the
        // project and, for source starts, accept/materialize its source. Do this before any draft details are
        // exposed through finalization errors.
        RequireAdministrator(actor);
        if (string.IsNullOrWhiteSpace(operationKey))
            throw new ProjectSetupInvalidException("Finalization requires an idempotency key.");

        // SAVE_BOUNDARY requires pre-save reads that participate in a shared unit to be inside that unit. The
        // serializable transaction prevents competing finalizers from committing separate outcomes. PostgreSQL
        // can abort the losing snapshot; that caller must retry in a fresh transaction to recover the result.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var draft = await db.ProjectSetupDrafts.SingleOrDefaultAsync(x => x.Id == draftId, ct)
            ?? throw new ProjectSetupNotFoundException();
        RequireManage(draft, actor);

        if (draft.State == ProjectSetupState.Completed)
            return CompletedResult(draft);
        if (draft.State == ProjectSetupState.Finalizing)
            throw new ProjectSetupConflictException("This setup is already being finalized. Retry after it completes.");
        // A discarded setup is not an editable draft any more, so it is refused before any validation or claim;
        // the recorded state stays truthful rather than resurfacing as a simple validation problem.
        if (draft.State == ProjectSetupState.Abandoned)
            throw new ProjectSetupInvalidException("This setup was discarded and cannot be finalized.");

        ValidateCommonAnswers(draft);
        if (draft.StartKind == ProjectSetupStartKind.Fresh) ValidateFreshAnswers(draft);
        else if (inception is null)
            throw new ProjectSetupInvalidException("The selected inception source service is unavailable.");
        try
        {
            if (!draft.BeginFinalization(expectedVersion, operationKey, DateTimeOffset.UtcNow))
                return CompletedResult(draft);
            // Claim first while keeping the transaction open. A competing snapshot cannot commit this same
            // draft version, so duplicate submissions cannot create a second project.
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

            CandidateBaseline? inceptionBaseline = null;
            if (draft.StartKind is ProjectSetupStartKind.AeroLinkBaseline or ProjectSetupStartKind.ExternalBaseline)
            {
                inceptionBaseline = new CandidateBaseline(draft.InceptionBaselineId, SoftwareBuildIdentifier.FromVersion(release.Version), 0,
                    project.Id, release.Id, null, "Inherited source working build", actor.UserName, now);
                db.CandidateBaselines.Add(inceptionBaseline);
                var effectivePolicy = new ResolvedProjectLadderPolicy(
                    ProjectLadderResolver.Resolve(ladder, LegacyLadderPolicy.Instance), LegacyLadderPolicy.Instance);
                await inception!.MaterializeAsync(draft, project, inceptionBaseline, actor, password,
                    sourceAssertionHash, sourceAssertionAccepted, effectivePolicy, ct);
                // The accepted source is the history of inception. The newly created build is a separate,
                // explicitly In-Work context that points at the materialized target baseline.
                db.SoftwareBuilds.Add(new SoftwareBuild(project.Id, release.Id, inceptionBaseline.Id,
                    release.CanonicalIdentity!, "Initial working build materialized from the accepted source.",
                    actor.UserName, now));
            }

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
                draft.StartKind == ProjectSetupStartKind.Fresh
                    ? $"Created Project {project.Id:D} from a fresh setup draft; no engineering content was inherited."
                    : draft.StartKind == ProjectSetupStartKind.AeroLinkBaseline
                        ? $"Created Project {project.Id:D} from {draft.StartKind} setup with exact source baseline {draft.SourceBaselineId:D}; source acceptance is not a new engineering approval."
                        : $"Created Project {project.Id:D} from {draft.StartKind} setup with exact source package {draft.SourceImportId:D}; source acceptance is not a new engineering approval.",
                "local", DateTimeOffset.UtcNow));
            var priorSealActor = db.LadderSealActor;
            db.LadderSealActor = actor.UserName;
            try { await db.SaveChangesAsync(ct); }
            finally { db.LadderSealActor = priorSealActor; }
            await transaction.CommitAsync(ct);
            return new(program.Id, project.Id, release.Id, release.Version, release.CanonicalIdentity!, false,
                resultJson);
        }
        catch (ProjectSetupConcurrencyException ex)
        {
            db.ChangeTracker.Clear();
            throw new ProjectSetupConflictException(
                "The setup changed before finalization could be claimed. Refresh and retry.", ex);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ProjectSetupConflictException("Another setup or project creation request won this draft.", ex);
        }
        catch (Exception ex) when (IsRetryableRace(ex))
        {
            db.ChangeTracker.Clear();
            throw new ProjectSetupConflictException(
                "Another finalization request changed this setup. Retry to recover its completed result.", ex);
        }
        catch (DbUpdateException ex) when (IsUniqueConflict(ex))
        {
            throw new ProjectSetupConflictException("A project identity or controlled release identity already exists.", ex);
        }
    }

    private static void ValidateCommonAnswers(ProjectSetupDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.ProjectName) || string.IsNullOrWhiteSpace(draft.SoftwareProduct))
            throw new ProjectSetupInvalidException("Project name and software product are required before finalization.");
        _ = SoftwareBuildIdentifier.Parse(draft.InitialReleaseVersion);
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
        if (ProjectSetupReviewRules.IsEmptyDefinition(draft.ReviewRulesJson))
            throw new ProjectSetupInvalidException("Review and approval rules must retain the concrete definition that was accepted.");
        ValidateRepository(draft.RepositoryJson);
        ValidateReviewRules(draft.ReviewRulesJson);
    }

    private async Task<ProjectSetupSourcePackage?> SelectedSourcePackageAsync(ProjectSetupDraft draft,
        CancellationToken ct)
    {
        if (draft.SourceImportId is Guid importId)
            return await db.ProjectSetupSourcePackages.SingleOrDefaultAsync(
                x => x.DraftId == draft.Id && x.Id == importId, ct);
        if (draft.SourceBaselineId is Guid baselineId)
            return await db.ProjectSetupSourcePackages.SingleOrDefaultAsync(
                x => x.DraftId == draft.Id && x.SourceBaselineId == baselineId, ct);
        return null;
    }

    private static void ValidateSourceBoundUpdate(ProjectSetupDraft draft, ProjectSetupUpdateCommand command,
        ProjectSetupSourcePackage? selectedSourcePackage)
    {
        // Fresh clears source-dependent answers while the durable package remains available for re-selection.
        if (command.StartKind == ProjectSetupStartKind.Fresh)
        {
            if (command.SelectedCategoriesJson is not null && !JsonEquivalent(command.SelectedCategoriesJson, "[]")
                || command.MappingJson is not null && !JsonEquivalent(command.MappingJson, "{}"))
                throw new ProjectSetupInvalidException("Fresh starts cannot inherit categories or source mappings.");
            return;
        }
        if (selectedSourcePackage is not null && command.StartKind is { } requestedKind
            && requestedKind != ProjectSetupStartKind.Fresh)
        {
            var matches = requestedKind == ProjectSetupStartKind.ExternalBaseline
                && selectedSourcePackage.Kind == ProjectSetupSourceKind.ExternalBaseline
                && command.SourceImportId == selectedSourcePackage.Id && command.SourceBaselineId is null
                || requestedKind == ProjectSetupStartKind.AeroLinkBaseline
                && selectedSourcePackage.Kind == ProjectSetupSourceKind.AeroLinkBaseline
                && command.SourceBaselineId == selectedSourcePackage.SourceBaselineId && command.SourceImportId is null;
            if (!matches)
                throw new ProjectSetupInvalidException("The setup source is owned by its staged package; choose it through the source controls.");
        }
        else if (command.StartKind is { } requestedKindWithoutPackage && requestedKindWithoutPackage != ProjectSetupStartKind.Fresh)
        {
            throw new ProjectSetupInvalidException("A baseline source must be captured or uploaded before it can be selected.");
        }

        if (command.StartKind is null && (command.SourceBaselineId is not null || command.SourceImportId is not null))
            throw new ProjectSetupInvalidException("Source identities require a supported staged source.");

        if (selectedSourcePackage is null) return;
        if (command.SelectedCategoriesJson is not null
            && !JsonEquivalent(command.SelectedCategoriesJson, selectedSourcePackage.SelectedCategoriesJson))
            throw new ProjectSetupInvalidException("Source categories are owned by the staged source configuration.");
        if (command.MappingJson is not null
            && !JsonEquivalent(command.MappingJson, selectedSourcePackage.MappingJson))
            throw new ProjectSetupInvalidException("Source mappings are owned by the staged source configuration.");
    }

    private static bool JsonEquivalent(string left, string right)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)); }
        catch (JsonException) { return false; }
    }

    private static void ValidateFreshAnswers(ProjectSetupDraft draft)
    {
        if (draft.StartKind != ProjectSetupStartKind.Fresh)
            throw new ProjectSetupInvalidException("Fresh validation was requested for a sourced setup.");
        if (draft.SourceBaselineId is not null || draft.SourceImportId is not null)
            throw new ProjectSetupInvalidException("A Fresh start cannot carry a source baseline or import.");
        var categories = Deserialize<string[]>(draft.SelectedCategoriesJson, "selected categories");
        if (categories.Length != 0)
            throw new ProjectSetupInvalidException("Fresh starts cannot inherit content categories.");
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
        var provider = GetPropertyOrNull(repository, "provider");
        if (provider is { ValueKind: not (JsonValueKind.String or JsonValueKind.Null) })
            throw new ProjectSetupInvalidException("Repository provider must be a string or null.");
        var providerText = provider is { ValueKind: JsonValueKind.String }
            ? provider.Value.GetString()?.Trim()
            : null;
        if (providerText is not null && !providerText.Equals("GitLab", StringComparison.OrdinalIgnoreCase))
            throw new ProjectSetupInvalidException("The supported repository provider is GitLab.");
        if (connectNow && string.IsNullOrWhiteSpace(providerText))
            throw new ProjectSetupInvalidException("A connected repository requires the GitLab provider.");
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

    private ProjectLadderConfiguration CreateLadder(ProjectSetupDraft draft, Guid projectId, DateTimeOffset now,
        string? ladderJson = null)
    {
        ladderJson ??= draft.LadderJson;
        // The final gate reads the same interpretation the readiness verdict and the offered standard used,
        // so an unrecognized artifact token is diagnosed by level and field instead of surfacing only as an
        // undifferentiated payload error.
        var reading = ProjectSetupLadderReader.Read(ladderJson);
        if (reading.IsDefault) return NewProjectLadderFactory.Create(projectId, now);
        if (reading.Findings.Count > 0)
            throw new ProjectSetupInvalidException(reading.Findings[0].Message, reading.Findings);
        IReadOnlyList<LadderStepDraft> steps;
        IReadOnlyList<LadderRelationshipDraft> relationships;
        try
        {
            (steps, relationships) = ProjectLadderDraftValidator.Validate(
                reading.Steps, reading.Relationships, LegacyLadderPolicy.Instance);
        }
        catch (DomainException ex)
        {
            throw new ProjectSetupInvalidException(ex.Message, ex, ProjectLadderDraftValidator.Inspect(
                reading.Steps, reading.Relationships, LegacyLadderPolicy.Instance));
        }

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
        using var parsed = JsonDocument.Parse(rulesJson);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object || !parsed.RootElement.EnumerateObject().Any())
            throw new ProjectSetupInvalidException("Review and approval rules must retain a concrete accepted definition.");

        // Definition semantics come from the same authority the readiness verdict uses. A configuration the
        // verdict reported as ready therefore cannot be refused here for a reason the verdict never saw, and
        // a refusal names the affected rule and stage instead of only the payload.
        var definitionFindings = ProjectSetupReviewRules.InspectRules(rulesJson);
        var definition = ProjectSetupReviewRules.InspectDefinition(rulesJson);
        if (!definition.HasRulesArray)
            throw new ProjectSetupInvalidException("The reviewed rules must contain a typed rules array.",
                definitionFindings);
        var applicable = ApplicableSubjects(ladder);
        if (definition.RuleCount == 0)
        {
            if (applicable.Count != 0)
                throw new ProjectSetupInvalidException("The reviewed rules must cover each applicable ladder subject.");
            return; // A Customer-only/non-verification ladder truthfully has no review workflows to offer.
        }
        // Subject names are compared the way the typed read resolves them, case-insensitively, so a
        // definition the wire contract accepts is not refused here for its letter case.
        var suppliedSubjects = definition.Subjects;
        var applicableNames = applicable.Select(x => x.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (suppliedSubjects.Distinct(StringComparer.OrdinalIgnoreCase).Count() != suppliedSubjects.Count
            || !applicableNames.SetEquals(suppliedSubjects))
            throw new ProjectSetupInvalidException("Review rules must cover each applicable ladder subject exactly once.");
        if (definitionFindings.Count > 0)
            throw new ProjectSetupInvalidException(definitionFindings[0].Message, definitionFindings);

        // Every rule and stage is now known to be readable by the workflow authority, so the typed read is
        // only the materialization step.
        var supplied = Deserialize<ReviewRulesWire>(rulesJson, "review rules");
        if (supplied.Rules is null)
            throw new ProjectSetupInvalidException("The reviewed rules must contain a typed rules array.");
        foreach (var suppliedRule in supplied.Rules)
        {
            var workflow = new ReviewWorkflow(projectId, suppliedRule.Name ?? suppliedRule.Subject.ToString(),
                suppliedRule.Subject, ReviewMode.Sequential,
                suppliedRule.Stages!.Select(x => new ReviewWorkflowStageDraft(x.Name, x.RequiredRole, x.Kind,
                    x.AuthorityKind)).ToArray(), actor, now);
            workflow.Activate(actor, now);
            db.ReviewWorkflows.Add(workflow);
        }
    }

    private static HashSet<ReviewSubject> ApplicableSubjects(ProjectLadderConfiguration ladder) =>
        ProjectSetupReviewRules.ApplicableSubjects(ladder);

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

    private static bool IsRetryableRace(Exception exception)
    {
        // Npgsql's execution strategy wraps transient provider errors in InvalidOperationException. Inspect
        // typed SQLSTATE through that chain, rather than matching arbitrary error-message text.
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is Npgsql.PostgresException { SqlState: "40001" or "40P01" }) return true;
        return false;
    }

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
    /// <summary>
    /// Structured, level-identified findings when the refusal came from a diagnosable configuration.
    /// Null means the message is the whole answer; callers must not synthesize findings to fill it.
    /// </summary>
    public IReadOnlyList<LadderFinding>? Findings { get; }

    public ProjectSetupInvalidException(string message) : base(message) { }
    public ProjectSetupInvalidException(string message, Exception inner) : base(message, inner) { }

    public ProjectSetupInvalidException(string message, IReadOnlyList<LadderFinding> findings)
        : base(message) => Findings = findings;

    public ProjectSetupInvalidException(string message, Exception inner, IReadOnlyList<LadderFinding> findings)
        : base(message, inner) => Findings = findings;
}
