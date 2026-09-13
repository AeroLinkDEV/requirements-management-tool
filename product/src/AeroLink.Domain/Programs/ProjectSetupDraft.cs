using AeroLink.Domain.Common;
using System.Security.Cryptography;
using System.Text;

namespace AeroLink.Domain.Programs;

/// <summary>The durable state of a project setup attempt. A draft is not a usable Project.</summary>
public enum ProjectSetupState { Draft, Finalizing, Completed, Abandoned }

/// <summary>The screen-level progress marker retained for resume and discovery.</summary>
public enum ProjectSetupStep { Details, StartingPoint, FirstBuild, Ladder, WorkingRules, Services, Review, Complete }

/// <summary>Supported inception sources. Non-Fresh paths are composed by the import/inheritance delivery.</summary>
public enum ProjectSetupStartKind { Fresh, AeroLinkBaseline, ExternalBaseline }

/// <summary>
/// An attributable, resumable setup aggregate. Answers are kept as validated JSON snapshots so the draft can
/// evolve with supported source-specific contracts without turning setup into a generic workflow engine.
/// </summary>
public sealed class ProjectSetupDraft
{
    private ProjectSetupDraft() { }

    public ProjectSetupDraft(Guid creatorUserId, string creatorUserName, string? initialProjectName = null)
    {
        if (creatorUserId == Guid.Empty) throw new DomainException("A setup draft requires its creator.");
        if (string.IsNullOrWhiteSpace(creatorUserName)) throw new DomainException("A setup draft requires an attributable creator.");
        if (initialProjectName?.Trim().Length > 200)
            throw new DomainException("Project name cannot exceed 200 characters.");

        Id = Guid.NewGuid();
        CreatorUserId = creatorUserId;
        CreatorUserName = creatorUserName.Trim().ToLowerInvariant();
        InternalProgramId = Guid.NewGuid();
        ProjectId = Guid.NewGuid();
        InitialReleaseId = Guid.NewGuid();
        // 30 characters is the current ProgramCode storage limit. This identity is generated once and is
        // never derived again when the visible Project name is edited later.
        InternalProgramCode = $"P-{Id:N}"[..30].ToUpperInvariant();
        InternalProgramName = BuildInternalProgramName(initialProjectName, Id);
        ProjectName = (initialProjectName ?? string.Empty).Trim();
        State = ProjectSetupState.Draft;
        CurrentStep = ProjectSetupStep.Details;
        StartKind = null;
        SelectedCategoriesJson = "[]";
        LadderJson = "{}";
        ReviewRulesJson = "{}";
        RepositoryJson = "{\"mode\":\"ConfigureLater\",\"status\":\"Pending\"}";
        MappingJson = "{}";
        CreatedAt = UpdatedAt = LastSavedAt = DateTimeOffset.UtcNow;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid CreatorUserId { get; private set; }
    public string CreatorUserName { get; private set; } = "";

    /// <summary>Reserved backing identities. They are not visible setup questions and are stable across retries.</summary>
    public Guid InternalProgramId { get; private set; }
    public string InternalProgramName { get; private set; } = "";
    public string InternalProgramCode { get; private set; } = "";
    public Guid ProjectId { get; private set; }
    public Guid InitialReleaseId { get; private set; }

    public ProjectSetupState State { get; private set; }
    public ProjectSetupStep CurrentStep { get; private set; }
    public string ProjectName { get; private set; } = "";
    public string SoftwareProduct { get; private set; } = "";
    public ProjectSetupStartKind? StartKind { get; private set; }
    public Guid? SourceBaselineId { get; private set; }
    public Guid? SourceImportId { get; private set; }
    public string InitialReleaseVersion { get; private set; } = "";
    public string InitialReleaseCanonicalIdentity { get; private set; } = "";
    public string SelectedCategoriesJson { get; private set; } = "[]";
    public string LadderJson { get; private set; } = "{}";
    public string ReviewRulesJson { get; private set; } = "{}";
    public string RepositoryJson { get; private set; } = "{}";
    public string MappingJson { get; private set; } = "{}";
    public bool ReviewRulesAccepted { get; private set; }
    /// <summary>Hash of the exact ladder and review-rule snapshots the creator accepted together.</summary>
    public string? ReviewRulesAcceptanceHash { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset LastSavedAt { get; private set; }
    public DateTimeOffset? FinalizationStartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? FinalizationOperationKey { get; private set; }
    public string? FinalizationResultJson { get; private set; }
    public Guid? CompletedProgramId { get; private set; }
    public Guid? CompletedProjectId { get; private set; }
    public Guid? CompletedReleaseId { get; private set; }

    public void UpdateAnswers(long expectedVersion, ProjectSetupStep currentStep, string? projectName,
        string? softwareProduct, ProjectSetupStartKind? startKind, Guid? sourceBaselineId, Guid? sourceImportId,
        string? initialReleaseVersion, string? selectedCategoriesJson, string? ladderJson, string? reviewRulesJson,
        bool? reviewRulesAccepted, string? repositoryJson, string? mappingJson, DateTimeOffset now)
    {
        EnsureEditable();
        EnsureVersion(expectedVersion);
        if (!Enum.IsDefined(currentStep)) throw new DomainException("The setup step is not supported.");
        if (projectName is not null && projectName.Trim().Length > 200) throw new DomainException("Project name cannot exceed 200 characters.");
        if (softwareProduct is not null && softwareProduct.Trim().Length > 200) throw new DomainException("Software product cannot exceed 200 characters.");

        if (projectName is not null) ProjectName = projectName.Trim();
        if (softwareProduct is not null) SoftwareProduct = softwareProduct.Trim();
        var configurationChanged = ladderJson is not null || reviewRulesJson is not null;
        if (configurationChanged)
        {
            ReviewRulesAccepted = false;
            ReviewRulesAcceptanceHash = null;
        }
        if (startKind is null)
        {
            if (sourceBaselineId is not null || sourceImportId is not null)
                throw new DomainException("Source identities require a supported setup source.");
        }
        else
        {
            ValidateSource(startKind.Value, sourceBaselineId, sourceImportId);
        }
        if (startKind is not null)
        {
            StartKind = startKind;
            SourceBaselineId = sourceBaselineId;
            SourceImportId = sourceImportId;
        }
        if (initialReleaseVersion is not null)
        {
            var parsed = SoftwareBuildIdentifier.Parse(initialReleaseVersion);
            InitialReleaseVersion = initialReleaseVersion.Trim();
            InitialReleaseCanonicalIdentity = parsed.OfficialName;
        }
        if (selectedCategoriesJson is not null) SelectedCategoriesJson = RequiredJson(selectedCategoriesJson, "selected categories");
        if (ladderJson is not null) LadderJson = RequiredJson(ladderJson, "ladder");
        if (reviewRulesJson is not null) ReviewRulesJson = RequiredJson(reviewRulesJson, "review rules");
        if (reviewRulesAccepted is not null)
        {
            ReviewRulesAccepted = reviewRulesAccepted.Value;
            ReviewRulesAcceptanceHash = reviewRulesAccepted.Value ? AcceptanceHash(LadderJson, ReviewRulesJson) : null;
        }
        if (repositoryJson is not null) RepositoryJson = RequiredJson(repositoryJson, "repository settings");
        if (mappingJson is not null) MappingJson = RequiredJson(mappingJson, "mapping");
        CurrentStep = currentStep;
        Touch(now);
    }

    /// <summary>Claims the finalization operation inside the caller's transaction.</summary>
    public bool BeginFinalization(long expectedVersion, string operationKey, DateTimeOffset now)
    {
        if (State == ProjectSetupState.Completed) return false;
        EnsureEditable();
        EnsureVersion(expectedVersion);
        if (string.IsNullOrWhiteSpace(operationKey)) throw new DomainException("Finalization requires an idempotency key.");
        State = ProjectSetupState.Finalizing;
        FinalizationOperationKey = operationKey.Trim();
        FinalizationStartedAt = now;
        Touch(now);
        return true;
    }

    public void Complete(string resultJson, Guid completedProgramId, Guid completedProjectId,
        Guid completedReleaseId, DateTimeOffset now)
    {
        if (State != ProjectSetupState.Finalizing) throw new DomainException("Only a claimed setup can be completed.");
        if (completedProgramId == Guid.Empty || completedProjectId == Guid.Empty || completedReleaseId == Guid.Empty)
            throw new DomainException("Finalization must retain the completed project identities.");
        FinalizationResultJson = RequiredJson(resultJson, "finalization result");
        CompletedProgramId = completedProgramId;
        CompletedProjectId = completedProjectId;
        CompletedReleaseId = completedReleaseId;
        State = ProjectSetupState.Completed;
        CompletedAt = now;
        CurrentStep = ProjectSetupStep.Complete;
        Touch(now);
    }

    public void RecoverInterruptedFinalization(DateTimeOffset now)
    {
        if (State != ProjectSetupState.Finalizing) return;
        State = ProjectSetupState.Draft;
        FinalizationStartedAt = null;
        FinalizationOperationKey = null;
        CurrentStep = ProjectSetupStep.Review;
        Touch(now);
    }

    public void Abandon(DateTimeOffset now)
    {
        if (State == ProjectSetupState.Completed) throw new DomainException("A completed setup cannot be discarded.");
        State = ProjectSetupState.Abandoned;
        Touch(now);
    }

    private void EnsureEditable()
    {
        if (State is ProjectSetupState.Completed or ProjectSetupState.Abandoned)
            throw new DomainException($"A setup in {State} state cannot be edited.");
        if (State == ProjectSetupState.Finalizing)
            throw new DomainException("This setup is already being finalized. Retry after it completes.");
    }

    private void EnsureVersion(long expectedVersion)
    {
        if (expectedVersion < 1 || expectedVersion != Version)
            throw new ProjectSetupConcurrencyException("This setup changed after it was opened. Refresh and retry your saved answers.");
    }

    private void Touch(DateTimeOffset now) { UpdatedAt = LastSavedAt = now; Version++; }

    private static string AcceptanceHash(string ladder, string rules) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{ladder}\n{rules}"))).ToLowerInvariant();

    private static void ValidateSource(ProjectSetupStartKind startKind, Guid? sourceBaselineId, Guid? sourceImportId)
    {
        if (!Enum.IsDefined(startKind)) throw new DomainException("The setup source is not supported.");
        if (startKind == ProjectSetupStartKind.Fresh && (sourceBaselineId is not null || sourceImportId is not null))
            throw new DomainException("A Fresh start cannot carry a source identity.");
        if (startKind == ProjectSetupStartKind.AeroLinkBaseline && (sourceBaselineId is null || sourceImportId is not null))
            throw new DomainException("An AeroLink baseline start requires only the exact baseline identity.");
        if (startKind == ProjectSetupStartKind.ExternalBaseline && (sourceImportId is null || sourceBaselineId is not null))
            throw new DomainException("An external baseline start requires only the staged import identity.");
    }

    private static string RequiredJson(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException($"The {field} payload is required.");
        return value.Trim();
    }

    private static string BuildInternalProgramName(string? projectName, Guid id)
    {
        var seed = string.IsNullOrWhiteSpace(projectName) ? "Project" : projectName.Trim();
        var value = $"{seed} backing scope {id:N}";
        return value.Length <= 200 ? value : value[..200];
    }
}

public sealed class ProjectSetupConcurrencyException(string message) : InvalidOperationException(message);
