using AeroLink.Domain.Common;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Traceability;

namespace AeroLink.Domain.Verification;

/// <summary>
/// The team/scope that owns verification work.  Artifact kind is deliberately a separate fact: a software
/// Case and a software Procedure belong to the same discipline even though their content and later identities
/// differ.
/// </summary>
public enum VerificationDiscipline
{
    System,
    HighLevelSoftware,
    LowLevelSoftware,
}

/// <summary>The two verification artifacts supported by the neutral profile vocabulary.</summary>
public enum VerificationArtifactKind
{
    Case,
    Procedure,
}

/// <summary>Version marker for the additive neutral verification profile representation.</summary>
public static class VerificationArtifactProfileSchema
{
    public const int Legacy = 1;
    public const int Current = 2;
    /// <summary>Named actor for governed platform migrations (e.g. #726). Never a human actor.</summary>
    public const string GovernedMigrationActor = "aerolink-migration";
}

/// <summary>Stable identity for one verification artifact family.</summary>
public readonly record struct VerificationArtifactKey(
    VerificationDiscipline Discipline,
    VerificationArtifactKind Kind)
{
    public override string ToString() => $"{Discipline}:{Kind}";
}

/// <summary>
/// Neutral shared seams for the current verification aggregate.  The existing procedure storage remains the
/// compatibility store in this slice; these contracts make identity/header/lifecycle and revision evidence
/// explicit without creating a parallel Case or Procedure aggregate.
/// </summary>
public enum VerificationArtifactLifecycleState { Draft, Active, Retired }

public interface IVerificationArtifactHeader
{
    Guid ArtifactId { get; }
    Guid ProjectId { get; }
    VerificationArtifactKey ArtifactKey { get; }
    string Identity { get; }
    string Title { get; }
    string OwnerId { get; }
}

public sealed record VerificationArtifactHeader(
    Guid ArtifactId,
    Guid ProjectId,
    VerificationArtifactKey ArtifactKey,
    string Identity,
    string Title,
    string OwnerId) : IVerificationArtifactHeader;

public interface IVerificationArtifactRevision
{
    Guid RevisionId { get; }
    Guid ArtifactId { get; }
    VerificationArtifactKind Kind { get; }
    int Revision { get; }
    VerificationArtifactLifecycleState State { get; }
    string AuthorId { get; }
    Guid? SourceTestChangeRequestId { get; }
    Guid? EffectiveBaselineId { get; }
    DateTimeOffset CreatedAt { get; }
}

public sealed record VerificationArtifactRevisionProvenance(
    Guid? SourceTestChangeRequestId,
    Guid? EffectiveBaselineId,
    string SourceChangeRequestsJson);

public sealed record VerificationArtifactRevisionHeader(
    Guid RevisionId,
    Guid ArtifactId,
    VerificationArtifactKind Kind,
    int Revision,
    VerificationArtifactLifecycleState State,
    string AuthorId,
    Guid? SourceTestChangeRequestId,
    Guid? EffectiveBaselineId,
    DateTimeOffset CreatedAt) : IVerificationArtifactRevision;

public interface IVerificationArtifactRevisionContent
{
    VerificationArtifactKind Kind { get; }
}

/// <summary>Compatibility projection for a Case revision; legacy field meaning is retained verbatim.</summary>
public sealed record VerificationCaseRevisionContent(
    string Objective,
    string Preconditions,
    string Steps,
    string ExpectedResult) : IVerificationArtifactRevisionContent
{
    public VerificationArtifactKind Kind => VerificationArtifactKind.Case;
}

/// <summary>
/// The procedural body of a software Procedure revision.  The four legacy properties are retained as a
/// compatibility projection for callers that still render the shared verification shell; the six named
/// fields are the authoritative Procedure vocabulary and are intentionally not used for Case revisions.
/// </summary>
public sealed record VerificationProcedureRevisionContent(
    string Objective,
    string Preconditions,
    string Steps,
    string ExpectedResult,
    string EnvironmentSetup = "",
    string TestData = "",
    string OrderedSteps = "",
    string ExpectedObservations = "",
    string Cleanup = "",
    string ToolingAutomation = "") : IVerificationArtifactRevisionContent
{
    public VerificationArtifactKind Kind => VerificationArtifactKind.Procedure;
    public string Setup => EnvironmentSetup;
    public string ExecutableSteps => OrderedSteps;
    public string ExpectedObservationsText => ExpectedObservations;
    public string Tooling => ToolingAutomation;
}

/// <summary>How a non-root software Procedure is related to exact Case revisions.</summary>
public enum VerificationProcedureParentKind
{
    Unspecified,
    Allocated,
    Derived,
}

/// <summary>
/// What a verification package's exact parent actually is. Distinct from <see cref="VerificationArtifactKind"/>,
/// which says what the package itself is: a Procedure's parent may be a Case or a requirement depending on
/// discipline, and conflating the two is how a System Procedure ends up reported as hanging off a Case.
/// </summary>
public enum VerificationParentArtifactKind { Requirement, Case }

/// <summary>
/// Compatibility seam for the existing Procedure parent API. Requirements and
/// Case/System coverage call the neutral policy directly so the XOR invariant
/// is not reimplemented per artifact type.
/// </summary>
public static class VerificationProcedureParentPolicy
{
    /// <summary>
    /// What kind of artifact a verification package's exact parents are.
    ///
    /// Not derivable from the artifact kind alone, which is the mistake this exists to stop. A System
    /// Procedure and a software Case both take requirement revisions as exact parents; only a software
    /// Procedure takes Case revisions. Reading "Procedure implies Case parent" reports a System Procedure as
    /// hanging off a Case it has no relationship with — see the field comment on
    /// <c>TestProcedureChange.ParentKind</c>, which is the authority this mirrors.
    /// </summary>
    public static VerificationParentArtifactKind ParentArtifactKind(
        VerificationDiscipline discipline, VerificationArtifactKind artifactKind) =>
        artifactKind == VerificationArtifactKind.Procedure && discipline != VerificationDiscipline.System
            ? VerificationParentArtifactKind.Case
            : VerificationParentArtifactKind.Requirement;

    public static ExactParentClassification Classification(VerificationProcedureParentKind kind) => kind switch
    {
        VerificationProcedureParentKind.Allocated => ExactParentClassification.Allocated,
        VerificationProcedureParentKind.Derived => ExactParentClassification.Derived,
        _ => ExactParentClassification.Unspecified,
    };

    public static void Validate(VerificationProcedureParentKind kind,
        IEnumerable<Guid>? caseRevisionIds, string? derivedRationale)
        => ExactParentSelectionPolicy.Validate(Classification(kind), caseRevisionIds,
            derivedRationale, "software Procedure revision");
}

/// <summary>Capabilities a routed consumer must explicitly declare for a v2 artifact registration.</summary>
[Flags]
public enum VerificationArtifactCapability
{
    None = 0,
    Identity = 1,
    Header = 2,
    Revision = 4,
    Lifecycle = 8,
    Coverage = 16,
    Execution = 32,
    ControlledDocument = 64,
    ChangeReview = 128,
}

/// <summary>
/// The code-owned identity and output bindings for one artifact key.  The old names remain aliases so current
/// consumers can move to key-based lookups without changing their visible output in this slice.
/// </summary>
public sealed record VerificationArtifactDefinition
{
    public VerificationArtifactDefinition(VerificationArtifactKey key, string artifactPrefix,
        string testChangeRequestPrefix, ReviewSubject reviewSubject, ControlledDocumentType controlledDocumentType,
        RequirementLevel assessmentTarget, VerificationArtifactCapability requiredCapabilities =
            VerificationArtifactCapability.Identity | VerificationArtifactCapability.Header |
            VerificationArtifactCapability.Revision | VerificationArtifactCapability.Lifecycle |
            VerificationArtifactCapability.Coverage | VerificationArtifactCapability.Execution |
            VerificationArtifactCapability.ControlledDocument | VerificationArtifactCapability.ChangeReview)
    {
        if (!Enum.IsDefined(key.Discipline) || !Enum.IsDefined(key.Kind))
            throw new DomainException("A verification artifact definition requires a known discipline and kind.");
        ArtifactPrefix = Required(artifactPrefix, "artifact prefix");
        TestChangeRequestPrefix = Required(testChangeRequestPrefix, "test-change-request prefix");
        ArtifactKey = key;
        ReviewSubject = reviewSubject;
        ControlledDocumentType = controlledDocumentType;
        AssessmentTarget = assessmentTarget;
        RequiredCapabilities = requiredCapabilities;
    }

    public VerificationArtifactKey ArtifactKey { get; }
    public VerificationArtifactKey Key => ArtifactKey;
    public string ArtifactPrefix { get; }
    public string Prefix => ArtifactPrefix;
    public string TestChangeRequestPrefix { get; }
    public string TcrPrefix => TestChangeRequestPrefix;
    public ReviewSubject ReviewSubject { get; }
    public ControlledDocumentType ControlledDocumentType { get; }
    public ControlledDocumentType DocumentType => ControlledDocumentType;
    public RequirementLevel AssessmentTarget { get; }
    public RequirementLevel RequirementLevel => AssessmentTarget;
    public TestProcedureLevel ProcedureLevel => AssessmentTarget switch
    {
        RequirementLevel.System => TestProcedureLevel.System,
        RequirementLevel.HighLevel => TestProcedureLevel.HighLevel,
        RequirementLevel.LowLevel => TestProcedureLevel.LowLevel,
        _ => throw new DomainException($"The artifact key {ArtifactKey} has no verification level target.")
    };
    public VerificationArtifactCapability RequiredCapabilities { get; }

    // These aliases make the definition useful to older projection code without making discipline the identity.
    public VerificationDiscipline Discipline => ArtifactKey.Discipline;
    public VerificationArtifactKind Kind => ArtifactKey.Kind;

    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new DomainException($"A verification artifact definition requires a {name}.")
        : value.Trim().ToUpperInvariant();
}

/// <summary>
/// An ordered, validated profile.  Executability is derived from the last enabled definition; no executable flag
/// is stored.  System has one Procedure, while software may be Case-only or Case followed by Procedure.
/// </summary>
public sealed class VerificationArtifactProfile
{
    private readonly IReadOnlyList<VerificationArtifactDefinition> definitions;

    public VerificationArtifactProfile(VerificationDiscipline discipline,
        IEnumerable<VerificationArtifactDefinition> definitions)
    {
        if (!Enum.IsDefined(discipline)) throw new DomainException("A verification profile requires a known discipline.");
        ArgumentNullException.ThrowIfNull(definitions);
        this.definitions = definitions.ToArray();
        Validate(discipline, this.definitions);
        Discipline = discipline;
    }

    public VerificationDiscipline Discipline { get; }
    public IReadOnlyList<VerificationArtifactDefinition> Definitions => definitions;
    public IReadOnlyList<VerificationArtifactKind> EnabledKinds => definitions.Select(x => x.Kind).ToArray();
    public bool HasVerification => definitions.Count > 0;
    public VerificationArtifactDefinition ExecutableArtifact => definitions.Count == 0
        ? throw new DomainException("A verification profile without artifacts has no executable artifact.")
        : definitions[^1];
    public VerificationArtifactKey ExecutableKey => ExecutableArtifact.Key;

    public bool Enables(VerificationArtifactKind kind) => definitions.Any(x => x.Kind == kind);

    public static VerificationArtifactProfile System => For(VerificationDiscipline.System, [VerificationArtifactKind.Procedure]);
    public static VerificationArtifactProfile HighLevelSoftware => For(VerificationDiscipline.HighLevelSoftware, [VerificationArtifactKind.Case]);
    public static VerificationArtifactProfile LowLevelSoftware => For(VerificationDiscipline.LowLevelSoftware, [VerificationArtifactKind.Case]);

    public static VerificationArtifactProfile For(VerificationDiscipline discipline,
        IEnumerable<VerificationArtifactKind> kinds) => new(discipline,
        kinds.Select(kind => VerificationArtifactVocabulary.Definition(new VerificationArtifactKey(discipline, kind))));

    public static VerificationArtifactProfile Create(VerificationDiscipline discipline,
        IEnumerable<VerificationArtifactDefinition> definitions) => new(discipline, definitions);

    public static VerificationArtifactProfile ForLegacy(RequirementLevel level,
        VerificationLevelBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var discipline = ToNeutral(binding.Discipline);
        var key = binding.ArtifactKey ?? new VerificationArtifactKey(discipline,
            binding.Discipline == TestChangeReviewDiscipline.System
                ? VerificationArtifactKind.Procedure
                : VerificationArtifactKind.Case);
        if (key.Discipline != discipline)
            throw new DomainException("A verification binding artifact key must match its discipline.");
        var vocabulary = VerificationArtifactVocabulary.Definition(key);
        var definition = new VerificationArtifactDefinition(key, vocabulary.ArtifactPrefix,
            vocabulary.TestChangeRequestPrefix, vocabulary.ReviewSubject, binding.DocumentType, level,
            vocabulary.RequiredCapabilities);
        return new(discipline, [definition]);
    }

    public static VerificationArtifactProfile ForSoftware(RequirementLevel level,
        VerificationLevelBinding binding, bool includeProcedure = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var discipline = ToNeutral(binding.Discipline);
        var caseKey = new VerificationArtifactKey(discipline, VerificationArtifactKind.Case);
        var caseVocabulary = VerificationArtifactVocabulary.Definition(caseKey);
        var caseDefinition = new VerificationArtifactDefinition(caseKey, caseVocabulary.ArtifactPrefix,
            caseVocabulary.TestChangeRequestPrefix, caseVocabulary.ReviewSubject, binding.DocumentType, level,
            caseVocabulary.RequiredCapabilities);
        if (!includeProcedure) return new(discipline, [caseDefinition]);
        var procedureKey = new VerificationArtifactKey(discipline, VerificationArtifactKind.Procedure);
        var procedureVocabulary = VerificationArtifactVocabulary.Definition(procedureKey);
        var procedure = new VerificationArtifactDefinition(procedureKey, procedureVocabulary.ArtifactPrefix,
            procedureVocabulary.TestChangeRequestPrefix, procedureVocabulary.ReviewSubject,
            procedureVocabulary.ControlledDocumentType, level,
            procedureVocabulary.RequiredCapabilities);
        return new(discipline, [caseDefinition, procedure]);
    }

    public static VerificationDiscipline ToNeutral(TestChangeReviewDiscipline discipline) => discipline switch
    {
        TestChangeReviewDiscipline.System => VerificationDiscipline.System,
        TestChangeReviewDiscipline.HighLevelSoftware => VerificationDiscipline.HighLevelSoftware,
        TestChangeReviewDiscipline.LowLevelSoftware => VerificationDiscipline.LowLevelSoftware,
        _ => throw new DomainException($"Unknown verification discipline: {discipline}.")
    };

    public static TestChangeReviewDiscipline ToLegacy(VerificationDiscipline discipline) => discipline switch
    {
        VerificationDiscipline.System => TestChangeReviewDiscipline.System,
        VerificationDiscipline.HighLevelSoftware => TestChangeReviewDiscipline.HighLevelSoftware,
        VerificationDiscipline.LowLevelSoftware => TestChangeReviewDiscipline.LowLevelSoftware,
        _ => throw new DomainException($"Unknown verification discipline: {discipline}.")
    };

    public static void ValidateEnabledKinds(VerificationDiscipline discipline,
        IEnumerable<VerificationArtifactKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var definitions = kinds.Select(kind => new VerificationArtifactDefinition(
            new VerificationArtifactKey(discipline, kind), "PLACEHOLDER", "PLACEHOLDER",
            ReviewSubject.SystemTest, ControlledDocumentType.SystemTestProcedures, RequirementLevel.System));
        Validate(discipline, definitions.ToArray());
    }

    public static string SerializeKinds(IEnumerable<VerificationArtifactKind> kinds) =>
        string.Join(',', kinds.Select(x => x.ToString()));

    public static IReadOnlyList<VerificationArtifactKind> ParseKinds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<VerificationArtifactKind>();
        foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Enum.TryParse<VerificationArtifactKind>(token, false, out var kind) || !Enum.IsDefined(kind))
                throw new DomainException($"Unknown persisted verification artifact kind '{token}'.");
            result.Add(kind);
        }
        return result;
    }

    private static void Validate(VerificationDiscipline discipline,
        IReadOnlyList<VerificationArtifactDefinition> definitions)
    {
        if (definitions.Any(x => x.Discipline != discipline))
            throw new DomainException("A verification profile cannot mix disciplines.");
        if (definitions.Select(x => x.Key).Distinct().Count() != definitions.Count)
            throw new DomainException("A verification profile cannot contain duplicate artifact keys.");
        if (discipline == VerificationDiscipline.System)
        {
            if (definitions.Count != 1 || definitions[0].Kind != VerificationArtifactKind.Procedure)
                throw new DomainException("System verification must have exactly one Procedure artifact.");
            return;
        }
        if (definitions.Count is < 1 or > 2 || definitions[0].Kind != VerificationArtifactKind.Case
            || (definitions.Count == 2 && definitions[1].Kind != VerificationArtifactKind.Procedure))
            throw new DomainException("Software verification must be [Case] or [Case, Procedure].");
    }

}

/// <summary>Code-owned ordered artifact definitions used to validate persisted project profile shapes.</summary>
public static class VerificationArtifactVocabulary
{
    public static IReadOnlyList<VerificationArtifactDefinition> Definitions { get; } = Build();

    public static VerificationArtifactDefinition Definition(VerificationArtifactKey key) =>
        Definitions.SingleOrDefault(x => x.Key == key)
        ?? throw new DomainException($"Unknown verification artifact key '{key}'.");

    public static IReadOnlyList<VerificationArtifactDefinition> ForDiscipline(VerificationDiscipline discipline) =>
        Definitions.Where(x => x.Discipline == discipline).ToArray();

    private static IReadOnlyList<VerificationArtifactDefinition> Build()
    {
        static VerificationArtifactDefinition D(VerificationDiscipline discipline, VerificationArtifactKind kind,
            string prefix, string tcr, ReviewSubject subject, ControlledDocumentType document, RequirementLevel target,
            VerificationArtifactCapability requiredCapabilities =
                VerificationArtifactCapability.Identity | VerificationArtifactCapability.Header |
                VerificationArtifactCapability.Revision | VerificationArtifactCapability.Lifecycle |
                VerificationArtifactCapability.Coverage | VerificationArtifactCapability.Execution |
                VerificationArtifactCapability.ControlledDocument | VerificationArtifactCapability.ChangeReview) =>
            new(new(discipline, kind), prefix, tcr, subject, document, target, requiredCapabilities);
        return
        [
            // A TCR is derived from the exact artifact key, not merely its discipline. Historical
            // software Case rows retain HLRTCCR/LLRTCCR; new software Procedure packages use their
            // own HLRTPCR/LLRTPCR families so the two review subjects can be configured independently.
            D(VerificationDiscipline.System, VerificationArtifactKind.Procedure, "SYSTP", "SYSTPCR", ReviewSubject.SystemTest, ControlledDocumentType.SystemTestProcedures, RequirementLevel.System),
            D(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case, "HLRTC", "HLRTCCR", ReviewSubject.HighLevelSoftwareCase, ControlledDocumentType.HighLevelTestCases, RequirementLevel.HighLevel),
            D(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure, "HLRTP", "HLRTPCR", ReviewSubject.HighLevelSoftwareProcedure, ControlledDocumentType.HighLevelTestProcedures, RequirementLevel.HighLevel),
            D(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Case, "LLRTC", "LLRTCCR", ReviewSubject.LowLevelSoftwareCase, ControlledDocumentType.LowLevelTestCases, RequirementLevel.LowLevel),
            D(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Procedure, "LLRTP", "LLRTPCR", ReviewSubject.LowLevelSoftwareProcedure, ControlledDocumentType.LowLevelTestProcedures, RequirementLevel.LowLevel),
        ];
    }
}
