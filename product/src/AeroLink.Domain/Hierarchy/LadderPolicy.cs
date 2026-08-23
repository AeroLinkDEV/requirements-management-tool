using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Hierarchy;

/// <summary>The capabilities that are part of a level's product-curated legacy bundle.</summary>
[Flags]
public enum LevelCapabilities
{
    None = 0,
    HasChangeControl = 1,
    HasVerification = 2,
    HasRequirementsDocument = 4,
    HasCodeTraceability = 8,
}

/// <summary>The controlled numbering/profile binding for a requirement change request.</summary>
public sealed record ChangeRequestLevelBinding(ChangeRequestType Type, RequirementLevel? SoftwareLevel, string Prefix);

/// <summary>The verification-side level, discipline, procedure prefix, and controlled document binding.</summary>
public sealed record VerificationLevelBinding(
    TestProcedureLevel ProcedureLevel,
    TestChangeReviewDiscipline Discipline,
    string ProcedurePrefix,
    ControlledDocumentType DocumentType,
    VerificationArtifactKey? ArtifactKey = null);

/// <summary>The fixed enterprise schema/specification projection for a requirement level.</summary>
public sealed record RequirementsCatalogueBinding(string SchemaKey, string SchemaName, string SpecificationNumber,
    string SpecificationTitle);

public enum LevelOriginKind { ChangeRequest, ExternalSourcePackage }

/// <summary>A complete product-curated definition for one requirement level.</summary>
public sealed class LevelDefinition
{
    public LevelDefinition(RequirementLevel level, string requirementPrefix, LevelCapabilities capabilities,
        ChangeRequestLevelBinding? changeRequest = null, VerificationLevelBinding? verification = null,
        ControlledDocumentType? requirementsDocumentType = null, RequirementsCatalogueBinding? requirementsCatalogue = null,
        string? testProcedureDocumentTitle = null, LevelOriginKind originKind = LevelOriginKind.ChangeRequest,
        VerificationArtifactProfile? verificationProfile = null)
    {
        if (!Enum.IsDefined(level)) throw new DomainException($"Unknown requirement level value: {(int)level}.");
        if (string.IsNullOrWhiteSpace(requirementPrefix)) throw new DomainException("A level requires a requirement prefix.");

        Level = level;
        RequirementPrefix = requirementPrefix.Trim().ToUpperInvariant();
        Capabilities = capabilities;
        ChangeRequest = changeRequest;
        Verification = verification;
        VerificationProfile = verificationProfile ?? (verification is null
            ? null
            : level == RequirementLevel.System
                ? VerificationArtifactProfile.ForLegacy(level, verification)
                : VerificationArtifactProfile.ForSoftware(level, verification));
        RequirementsDocumentType = requirementsDocumentType;
        RequirementsCatalogue = requirementsCatalogue;
        TestProcedureDocumentTitle = string.IsNullOrWhiteSpace(testProcedureDocumentTitle)
            ? null
            : testProcedureDocumentTitle.Trim();
        OriginKind = originKind;

        RequireBinding(LevelCapabilities.HasChangeControl, changeRequest is not null,
            "HasChangeControl requires a change-request binding.");
        RequireBinding(LevelCapabilities.HasVerification, verification is not null,
            "HasVerification requires a verification binding.");
        RequireBinding(LevelCapabilities.HasRequirementsDocument, requirementsDocumentType is not null,
            "HasRequirementsDocument requires a requirements-document binding.");
        RequireDisabled(LevelCapabilities.HasChangeControl, changeRequest is null,
            "A disabled change-control capability cannot carry a binding.");
        RequireDisabled(LevelCapabilities.HasVerification, verification is null,
            "A disabled verification capability cannot carry a binding.");
        RequireDisabled(LevelCapabilities.HasRequirementsDocument, requirementsDocumentType is null,
            "A disabled requirements-document capability cannot carry a binding.");
        RequireDisabled(LevelCapabilities.HasRequirementsDocument, requirementsCatalogue is null,
            "A disabled requirements-document capability cannot carry a requirements catalogue binding.");
        RequireDisabled(LevelCapabilities.HasVerification, TestProcedureDocumentTitle is null,
            "A disabled verification capability cannot carry a verification document title.");
        if (Has(LevelCapabilities.HasVerification) && VerificationProfile is null)
            throw new DomainException("A verification capability requires a verification artifact profile.");
        if (!Has(LevelCapabilities.HasVerification) && VerificationProfile is not null)
            throw new DomainException("A disabled verification capability cannot carry a verification artifact profile.");
        if (VerificationProfile is not null && verification is not null
            && VerificationProfile.Discipline != VerificationArtifactProfile.ToNeutral(verification.Discipline))
            throw new DomainException("A verification artifact profile must match its verification discipline.");

        if (changeRequest is not null)
        {
            if (string.IsNullOrWhiteSpace(changeRequest.Prefix))
                throw new DomainException("A change-request binding requires a prefix.");
            var expected = level switch
            {
                RequirementLevel.System => (ChangeRequestType.System, (RequirementLevel?)null),
                RequirementLevel.Interface => (ChangeRequestType.Interface, (RequirementLevel?)null),
                RequirementLevel.HighLevel or RequirementLevel.LowLevel =>
                    (ChangeRequestType.Software, (RequirementLevel?)level),
                _ => throw new DomainException($"The {level} level cannot carry a change-request binding."),
            };
            if (changeRequest.Type != expected.Item1 || changeRequest.SoftwareLevel != expected.Item2)
                throw new DomainException($"The {level} change-request binding does not match its level.");
        }

        if (requirementsDocumentType is not null
            && requirementsDocumentType is not (ControlledDocumentType.Sysrd or ControlledDocumentType.SwrdHighLevel or ControlledDocumentType.SwrdLowLevel))
            throw new DomainException($"The {level} requirements-document binding must name a requirements document.");

        if (verification is not null)
        {
            if (string.IsNullOrWhiteSpace(verification.ProcedurePrefix))
                throw new DomainException("A verification binding requires an artifact prefix.");
            if (ProcedureLevelFor(level) != verification.ProcedureLevel
                || DisciplineFor(level) != verification.Discipline
                || TestDocumentFor(level) != verification.DocumentType)
                throw new DomainException($"The {level} verification binding does not match its level.");
        }
        if (originKind == LevelOriginKind.ExternalSourcePackage
            && (changeRequest is not null || verification is not null || requirementsDocumentType is not null
                || requirementsCatalogue is not null || testProcedureDocumentTitle is not null))
            throw new DomainException("An external-origin level cannot carry change control, verification, or document bindings.");
    }

    public RequirementLevel Level { get; }
    public string RequirementPrefix { get; }
    public LevelCapabilities Capabilities { get; }
    public ChangeRequestLevelBinding? ChangeRequest { get; }
    public VerificationLevelBinding? Verification { get; }
    /// <summary>The neutral, ordered artifact vocabulary for this level; null means no verification capability.</summary>
    public VerificationArtifactProfile? VerificationProfile { get; }
    public ControlledDocumentType? RequirementsDocumentType { get; }
    public RequirementsCatalogueBinding? RequirementsCatalogue { get; }
    public string? TestProcedureDocumentTitle { get; }
    public LevelOriginKind OriginKind { get; }
    public bool UsesExternalOrigin => OriginKind == LevelOriginKind.ExternalSourcePackage;

    public bool Has(LevelCapabilities capability) => (Capabilities & capability) == capability;

    private void RequireBinding(LevelCapabilities capability, bool supplied, string message)
    {
        if (Has(capability) && !supplied) throw new DomainException(message);
    }

    private void RequireDisabled(LevelCapabilities capability, bool absent, string message)
    {
        if (!Has(capability) && !absent) throw new DomainException(message);
    }

    private static TestProcedureLevel ProcedureLevelFor(RequirementLevel level) => level switch
    {
        RequirementLevel.System => TestProcedureLevel.System,
        RequirementLevel.HighLevel => TestProcedureLevel.HighLevel,
        RequirementLevel.LowLevel => TestProcedureLevel.LowLevel,
        _ => throw new DomainException($"Unknown requirement level value: {(int)level}.")
    };

    private static TestChangeReviewDiscipline DisciplineFor(RequirementLevel level) => level switch
    {
        RequirementLevel.System => TestChangeReviewDiscipline.System,
        RequirementLevel.HighLevel => TestChangeReviewDiscipline.HighLevelSoftware,
        RequirementLevel.LowLevel => TestChangeReviewDiscipline.LowLevelSoftware,
        _ => throw new DomainException($"Unknown requirement level value: {(int)level}.")
    };

    private static ControlledDocumentType TestDocumentFor(RequirementLevel level) => level switch
    {
        RequirementLevel.System => ControlledDocumentType.SystemTestProcedures,
        RequirementLevel.HighLevel => ControlledDocumentType.HighLevelTestCases,
        RequirementLevel.LowLevel => ControlledDocumentType.LowLevelTestCases,
        _ => throw new DomainException($"Unknown requirement level value: {(int)level}.")
    };
}

/// <summary>A valid parent relationship in the fixed three-level ladder.</summary>
public sealed record LevelRelationship(RequirementLevel Parent, RequirementLevel Child);

/// <summary>
/// The single hierarchy authority used by runtime consumers. This slice exposes the current fixed product
/// policy only; it deliberately has no project configuration or persistence.
/// </summary>
public interface ILadderPolicy
{
    IReadOnlyList<RequirementLevel> OrderedLevels { get; }
    IReadOnlyList<LevelDefinition> Definitions { get; }
    IReadOnlyList<LevelRelationship> ParentRelationships { get; }
    IReadOnlyList<ControlledDocumentType> ControlledDocumentTypes { get; }
    LevelDefinition Definition(RequirementLevel level);
    IReadOnlyList<RequirementLevel> ParentLevels(RequirementLevel child);
    IReadOnlyList<RequirementLevel> DownstreamLevels(RequirementLevel source);
    TestProcedureLevel ProcedureLevel(RequirementLevel level);
    VerificationArtifactProfile VerificationProfile(RequirementLevel level);
    VerificationArtifactDefinition VerificationArtifact(VerificationArtifactKey key);
    VerificationArtifactKey ExecutableArtifactKey(RequirementLevel level);
    string ArtifactPrefix(VerificationArtifactKey key);
    string TestChangeRequestPrefix(VerificationArtifactKey key);
    ReviewSubject WorkflowSubject(VerificationArtifactKey key);
    ControlledDocumentType ControlledDocument(VerificationArtifactKey key);
    RequirementLevel AssessmentTarget(VerificationArtifactKey key);
    RequirementLevel RequirementLevelFor(TestProcedureLevel level);
    TestChangeReviewDiscipline Discipline(RequirementLevel level);
    RequirementLevel RequirementLevelFor(TestChangeReviewDiscipline discipline);
    ControlledDocumentType RequirementsDocument(RequirementLevel level);
    ControlledDocumentType TestProcedureDocument(RequirementLevel level);
    string TestProcedureDocumentTitle(RequirementLevel level);
    string ControlledDocumentPrefix(ControlledDocumentType type);
    string ControlledDocumentTitle(ControlledDocumentType type);
    string RequirementPrefix(RequirementLevel level);
    string ChangeRequestPrefix(ChangeRequestType type, RequirementLevel? softwareLevel);
    bool IsChangeRequestScopeValid(ChangeRequestType type, RequirementLevel? softwareLevel);
    bool AcceptsChangeRequest(ChangeRequestType type, RequirementLevel? softwareLevel, RequirementLevel level);
    string TestProcedurePrefix(TestProcedureLevel level);
    bool IsKnownTestProcedurePrefix(string baseNumber);
    string TestChangeReviewPrefix(TestChangeReviewDiscipline discipline);
    ReviewSubject WorkflowSubject(ChangeRequestType type);
    ReviewSubject WorkflowSubject(TestChangeReviewDiscipline discipline);
    /// <summary>ReqIF's characterized legacy compatibility rule: unknown or missing level means System.</summary>
    RequirementLevel ParseImportedRequirementLevel(string? value);
    bool TryParseRequirementLevel(string? value, out RequirementLevel level);
    bool AcceptsChangeRequest(ChangeRequestType type, RequirementLevel level);
    bool IsDownstreamTarget(RequirementLevel level);
    bool HasCodeTraceability(RequirementLevel level);
}

/// <summary>Explicit marker for #702's characterized legacy ladder compatibility behavior.</summary>
public interface ILegacyLadderCompatibilityPolicy { }

/// <summary>The current legacy/default composition of the ladder policy.</summary>
public sealed class LegacyLadderPolicy : ILadderPolicy, ILegacyLadderCompatibilityPolicy
{
    public static LegacyLadderPolicy Instance { get; } = new();

    private static readonly IReadOnlyList<RequirementLevel> Levels =
        [RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel];
    private static readonly IReadOnlyList<LevelRelationship> Relationships =
        [new(RequirementLevel.System, RequirementLevel.HighLevel), new(RequirementLevel.HighLevel, RequirementLevel.LowLevel)];
    private static readonly IReadOnlyList<LevelDefinition> LevelDefinitions =
    [
        new(RequirementLevel.System, "SYSR", LevelCapabilities.HasChangeControl | LevelCapabilities.HasVerification | LevelCapabilities.HasRequirementsDocument,
            new(ChangeRequestType.System, null, "SRCR"),
            new(TestProcedureLevel.System, TestChangeReviewDiscipline.System, "SYSTP", ControlledDocumentType.SystemTestProcedures),
            ControlledDocumentType.Sysrd,
            new("SYSTEM-REQ", "System Requirement", "SYSRD-000001", "System Requirements Document"),
            "System Test Procedures Document"),
        new(RequirementLevel.HighLevel, "HLR", LevelCapabilities.HasChangeControl | LevelCapabilities.HasVerification | LevelCapabilities.HasRequirementsDocument,
            new(ChangeRequestType.Software, RequirementLevel.HighLevel, "HLRCR"),
            new(TestProcedureLevel.HighLevel, TestChangeReviewDiscipline.HighLevelSoftware, "HLRTC", ControlledDocumentType.HighLevelTestCases),
            ControlledDocumentType.SwrdHighLevel,
            new("HLR", "High-Level Software Requirement", "HLRD-000001", "High-Level Software Requirements Document"),
            "High-Level Software Test Cases Document"),
        new(RequirementLevel.LowLevel, "LLR", LevelCapabilities.HasChangeControl | LevelCapabilities.HasVerification | LevelCapabilities.HasRequirementsDocument | LevelCapabilities.HasCodeTraceability,
            new(ChangeRequestType.Software, RequirementLevel.LowLevel, "LLRCR"),
            new(TestProcedureLevel.LowLevel, TestChangeReviewDiscipline.LowLevelSoftware, "LLRTC", ControlledDocumentType.LowLevelTestCases),
            ControlledDocumentType.SwrdLowLevel,
            new("LLR", "Low-Level Software Requirement", "LLRD-000001", "Low-Level Software Requirements Document"),
            "Low-Level Software Test Cases Document"),
    ];
    private static readonly IReadOnlyList<ControlledDocumentType> Documents =
    [
        ControlledDocumentType.Sysrd, ControlledDocumentType.SwrdHighLevel, ControlledDocumentType.SwrdLowLevel,
        ControlledDocumentType.SystemTestProcedures, ControlledDocumentType.HighLevelTestProcedures,
        ControlledDocumentType.LowLevelTestProcedures, ControlledDocumentType.HighLevelTestCases,
        ControlledDocumentType.LowLevelTestCases,
    ];

    public IReadOnlyList<RequirementLevel> OrderedLevels => Levels;
    public IReadOnlyList<LevelDefinition> Definitions => LevelDefinitions;
    public IReadOnlyList<LevelRelationship> ParentRelationships => Relationships;
    public IReadOnlyList<ControlledDocumentType> ControlledDocumentTypes => Documents;

    private static readonly LevelDefinition CustomerDefinition = new(RequirementLevel.Customer, "CUSR", LevelCapabilities.None,
        originKind: LevelOriginKind.ExternalSourcePackage);
    /// <summary>
    /// Interface Control Documents are authored in AeroLink and therefore carry a change-request profile,
    /// but they are not verification targets and do not produce a generated requirements document. Their
    /// revisions remain ordinary controlled requirements so System requirements can allocate upward to them.
    /// </summary>
    private static readonly LevelDefinition InterfaceDefinition = new(
        RequirementLevel.Interface, "ICDR", LevelCapabilities.HasChangeControl,
        new(ChangeRequestType.Interface, null, ChangeRequestNumbering.InterfacePrefix));
    public LevelDefinition Definition(RequirementLevel level) => LevelDefinitions.SingleOrDefault(x => x.Level == level)
        ?? (level switch
        {
            RequirementLevel.Customer => CustomerDefinition,
            RequirementLevel.Interface => InterfaceDefinition,
            _ => throw Unknown(level),
        });

    public IReadOnlyList<RequirementLevel> ParentLevels(RequirementLevel child)
    {
        _ = Definition(child);
        return Relationships.Where(x => x.Child == child).Select(x => x.Parent).ToArray();
    }

    public IReadOnlyList<RequirementLevel> DownstreamLevels(RequirementLevel source)
    {
        _ = Definition(source);
        return Relationships.Where(x => x.Parent == source).Select(x => x.Child).ToArray();
    }

    public TestProcedureLevel ProcedureLevel(RequirementLevel level) => Definition(level).Verification!.ProcedureLevel;

    public VerificationArtifactProfile VerificationProfile(RequirementLevel level) =>
        Definition(level).VerificationProfile
        ?? throw new DomainException($"The {level} definition has no verification profile.");

    public VerificationArtifactDefinition VerificationArtifact(VerificationArtifactKey key) =>
        VerificationArtifactVocabulary.Definition(key);

    public string ArtifactPrefix(VerificationArtifactKey key) => VerificationArtifact(key).ArtifactPrefix;
    public string TestChangeRequestPrefix(VerificationArtifactKey key) => VerificationArtifact(key).TestChangeRequestPrefix;
    public ReviewSubject WorkflowSubject(VerificationArtifactKey key) => VerificationArtifact(key).ReviewSubject;
    public ControlledDocumentType ControlledDocument(VerificationArtifactKey key) => VerificationArtifact(key).ControlledDocumentType;
    public RequirementLevel AssessmentTarget(VerificationArtifactKey key) => VerificationArtifact(key).AssessmentTarget;

    public VerificationArtifactKey ExecutableArtifactKey(RequirementLevel level) =>
        VerificationProfile(level).ExecutableKey;

    public RequirementLevel RequirementLevelFor(TestProcedureLevel level) => level switch
    {
        TestProcedureLevel.System => RequirementLevel.System,
        TestProcedureLevel.HighLevel => RequirementLevel.HighLevel,
        TestProcedureLevel.LowLevel => RequirementLevel.LowLevel,
        _ => throw Unknown(level),
    };

    public TestChangeReviewDiscipline Discipline(RequirementLevel level) => Definition(level).Verification!.Discipline;

    public RequirementLevel RequirementLevelFor(TestChangeReviewDiscipline discipline) => discipline switch
    {
        TestChangeReviewDiscipline.System => RequirementLevel.System,
        TestChangeReviewDiscipline.HighLevelSoftware => RequirementLevel.HighLevel,
        TestChangeReviewDiscipline.LowLevelSoftware => RequirementLevel.LowLevel,
        _ => throw Unknown(discipline),
    };

    public ControlledDocumentType RequirementsDocument(RequirementLevel level) => Definition(level).RequirementsDocumentType
        ?? throw new DomainException($"The {level} definition has no requirements document.");

    public ControlledDocumentType TestProcedureDocument(RequirementLevel level) =>
        VerificationProfile(level).ExecutableArtifact.DocumentType;

    public string TestProcedureDocumentTitle(RequirementLevel level) => Definition(level).TestProcedureDocumentTitle
        ?? throw new DomainException($"The {level} definition has no verification document title.");

    public string ControlledDocumentPrefix(ControlledDocumentType type) => type switch
    {
        ControlledDocumentType.Sysrd => "SYSRD",
        ControlledDocumentType.SwrdHighLevel => "HLRD",
        ControlledDocumentType.SwrdLowLevel => "LLRD",
        ControlledDocumentType.SystemTestProcedures => "SYSTD",
        ControlledDocumentType.HighLevelTestProcedures => "HLRTD",
        ControlledDocumentType.LowLevelTestProcedures => "LLRTD",
        ControlledDocumentType.HighLevelTestCases => "HLRTD",
        ControlledDocumentType.LowLevelTestCases => "LLRTD",
        _ => throw Unknown(type),
    };

    public string ControlledDocumentTitle(ControlledDocumentType type) => type switch
    {
        ControlledDocumentType.Sysrd => "System Requirements Document",
        ControlledDocumentType.SwrdHighLevel => "High-Level Software Requirements Document",
        ControlledDocumentType.SwrdLowLevel => "Low-Level Software Requirements Document",
        ControlledDocumentType.SystemTestProcedures => "System Test Procedures",
        ControlledDocumentType.HighLevelTestProcedures => "HLR Test Procedures",
        ControlledDocumentType.LowLevelTestProcedures => "LLR Test Procedures",
        ControlledDocumentType.HighLevelTestCases => "HLR Test Cases",
        ControlledDocumentType.LowLevelTestCases => "LLR Test Cases",
        _ => throw Unknown(type),
    };

    public string RequirementPrefix(RequirementLevel level) => Definition(level).RequirementPrefix;

    public string ChangeRequestPrefix(ChangeRequestType type, RequirementLevel? softwareLevel)
    {
        // The preview/numbering helper has always ignored an optional software level for System requests.
        // Keep that compatibility behavior here; IsChangeRequestScopeValid remains the strict authority for
        // whether a System aggregate may actually declare software scope.
        if (type == ChangeRequestType.System)
            return Definition(RequirementLevel.System).ChangeRequest!.Prefix;
        if (type == ChangeRequestType.Interface)
            return InterfaceDefinition.ChangeRequest!.Prefix;

        var level = type switch
        {
            ChangeRequestType.Software when softwareLevel is RequirementLevel.HighLevel or RequirementLevel.LowLevel => softwareLevel.Value,
            ChangeRequestType.Software => throw new DomainException("A software change request must declare HLR or LLR scope before it can be numbered."),
            _ => throw Unknown(type),
        };
        return Definition(level).ChangeRequest!.Prefix;
    }

    public bool IsChangeRequestScopeValid(ChangeRequestType type, RequirementLevel? softwareLevel)
    {
        if (type == ChangeRequestType.System)
            return softwareLevel is null && Definition(RequirementLevel.System).ChangeRequest?.Type == ChangeRequestType.System;
        if (type == ChangeRequestType.Interface)
            return softwareLevel is null && InterfaceDefinition.ChangeRequest?.Type == ChangeRequestType.Interface;
        if (type != ChangeRequestType.Software || softwareLevel is not (RequirementLevel.HighLevel or RequirementLevel.LowLevel))
            return false;
        var binding = Definition(softwareLevel.Value).ChangeRequest;
        return binding?.Type == ChangeRequestType.Software && binding.SoftwareLevel == softwareLevel;
    }

    public bool AcceptsChangeRequest(ChangeRequestType type, RequirementLevel? softwareLevel, RequirementLevel level)
    {
        if (!IsChangeRequestScopeValid(type, softwareLevel)) return false;
        return AcceptsChangeRequest(type, level)
            && (type != ChangeRequestType.Software || softwareLevel == level);
    }

    public string TestProcedurePrefix(TestProcedureLevel level) =>
        Definitions.SingleOrDefault(x => x.Verification?.ProcedureLevel == level)?.VerificationProfile?.ExecutableArtifact.ArtifactPrefix
        ?? throw Unknown(level);

    public bool IsKnownTestProcedurePrefix(string baseNumber) =>
        !string.IsNullOrWhiteSpace(baseNumber)
        && VerificationArtifactVocabulary.Definitions.Any(definition =>
            baseNumber.StartsWith(definition.ArtifactPrefix + "-", StringComparison.OrdinalIgnoreCase));

    public string TestChangeReviewPrefix(TestChangeReviewDiscipline discipline) =>
        Definitions.SingleOrDefault(x => x.Verification?.Discipline == discipline)?.VerificationProfile?.ExecutableArtifact.TestChangeRequestPrefix
        ?? throw Unknown(discipline);

    public ReviewSubject WorkflowSubject(ChangeRequestType type) => type switch
    {
        ChangeRequestType.System => ReviewSubject.System,
        ChangeRequestType.Software => ReviewSubject.Software,
        ChangeRequestType.Interface => ReviewSubject.Interface,
        _ => throw Unknown(type),
    };

    public ReviewSubject WorkflowSubject(TestChangeReviewDiscipline discipline) =>
        Definitions.SingleOrDefault(x => x.Verification?.Discipline == discipline)?.VerificationProfile?.ExecutableArtifact.ReviewSubject
        ?? throw Unknown(discipline);

    public RequirementLevel ParseImportedRequirementLevel(string? value)
    {
        if (string.Equals(value?.Trim(), nameof(RequirementLevel.HighLevel), StringComparison.Ordinal))
            return RequirementLevel.HighLevel;
        if (string.Equals(value?.Trim(), nameof(RequirementLevel.LowLevel), StringComparison.Ordinal))
            return RequirementLevel.LowLevel;
        if (string.Equals(value?.Trim(), nameof(RequirementLevel.Interface), StringComparison.Ordinal)
            || string.Equals(value?.Trim(), "ICD", StringComparison.Ordinal))
            return RequirementLevel.Interface;
        // The importer has always treated both an absent legacy level and an unrecognised one as System.
        // Keep that compatibility fallback explicit and isolated from the fail-closed policy methods above.
        return RequirementLevel.System;
    }

    public bool TryParseRequirementLevel(string? value, out RequirementLevel level)
    {
        var normalized = (value ?? "").Replace(" ", "").Replace("-", "").ToLowerInvariant();
        level = normalized switch
        {
            "system" or "sysr" => RequirementLevel.System,
            "highlevel" or "hlr" => RequirementLevel.HighLevel,
            "lowlevel" or "llr" => RequirementLevel.LowLevel,
            "customer" or "cusr" => RequirementLevel.Customer,
            "interface" or "icd" or "icdr" => RequirementLevel.Interface,
            _ => (RequirementLevel)(-1),
        };
        return (int)level >= 0;
    }

    public bool AcceptsChangeRequest(ChangeRequestType type, RequirementLevel level)
    {
        var definition = Definition(level);
        return definition.Has(LevelCapabilities.HasChangeControl)
            && definition.ChangeRequest?.Type == type;
    }

    public bool IsDownstreamTarget(RequirementLevel level)
    {
        _ = Definition(level);
        return Relationships.Any(x => x.Child == level);
    }

    public bool HasCodeTraceability(RequirementLevel level) => Definition(level).Has(LevelCapabilities.HasCodeTraceability);

    private static DomainException Unknown<T>(T value) => new($"Unknown ladder policy value: {value}.");
}

/// <summary>
/// A policy compiled from a resolved project ladder.  The persisted ladder only supplies catalogue presence,
/// capabilities, and direct parent/child edges; prefixes and the other product bindings remain code-owned.
/// Stored and Active project configurations use this compiled policy as their effective runtime authority;
/// injected instances remain useful for isolated domain tests and compatibility overloads.
/// </summary>
public class ResolvedProjectLadderPolicy : ILadderPolicy
{
    private readonly ILadderPolicy catalogue;
    private readonly IReadOnlyList<RequirementLevel> levels;
    private readonly IReadOnlyList<LevelDefinition> definitions;
    private readonly IReadOnlyList<LevelRelationship> relationships;
    private readonly IReadOnlyList<ControlledDocumentType> documents;

    public ResolvedProjectLadderPolicy(ResolvedProjectLadder resolved, ILadderPolicy? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        this.catalogue = catalogue ?? LegacyLadderPolicy.Instance;
        levels = resolved.Steps.OrderBy(x => x.Position).Select(x => x.Level).ToArray();
        definitions = resolved.Steps.OrderBy(x => x.Position).Select(x =>
        {
            var source = this.catalogue.Definition(x.Level);
            var capabilities = x.Capabilities;
            VerificationArtifactProfile? profile = null;
            if (capabilities.HasFlag(LevelCapabilities.HasVerification))
            {
                var kinds = x.EnabledArtifactKinds is { Count: > 0 }
                    ? x.EnabledArtifactKinds
                    : source.VerificationProfile?.EnabledKinds
                        ?? throw new DomainException($"The {x.Level} definition has no verification profile.");
                var discipline = VerificationArtifactProfile.ToNeutral(source.Verification!.Discipline);
                profile = new VerificationArtifactProfile(discipline, kinds.Select(kind =>
                    VerificationArtifactVocabulary.Definition(new VerificationArtifactKey(discipline, kind))));
            }
            return new LevelDefinition(x.Level, source.RequirementPrefix, capabilities,
                capabilities.HasFlag(LevelCapabilities.HasChangeControl) ? source.ChangeRequest : null,
                capabilities.HasFlag(LevelCapabilities.HasVerification) ? source.Verification : null,
                capabilities.HasFlag(LevelCapabilities.HasRequirementsDocument) ? source.RequirementsDocumentType : null,
                capabilities.HasFlag(LevelCapabilities.HasRequirementsDocument) ? source.RequirementsCatalogue : null,
                capabilities.HasFlag(LevelCapabilities.HasVerification) ? source.TestProcedureDocumentTitle : null,
                source.OriginKind, profile);
        }).ToArray();
        relationships = resolved.AllowedUpstream.Select(x => new LevelRelationship(x.Parent, x.Child)).ToArray();
        documents = definitions.Where(x => x.RequirementsDocumentType is not null).Select(x => x.RequirementsDocumentType!.Value)
            .Concat(definitions.Where(x => x.Verification is not null).Select(x => x.Verification!.DocumentType))
            .Distinct().ToArray();
    }

    public IReadOnlyList<RequirementLevel> OrderedLevels => levels;
    public IReadOnlyList<LevelDefinition> Definitions => definitions;
    public IReadOnlyList<LevelRelationship> ParentRelationships => relationships;
    public IReadOnlyList<ControlledDocumentType> ControlledDocumentTypes => documents;
    public LevelDefinition Definition(RequirementLevel level) => definitions.SingleOrDefault(x => x.Level == level)
        ?? throw new DomainException($"The project ladder does not configure {level}.");
    public IReadOnlyList<RequirementLevel> ParentLevels(RequirementLevel child)
    {
        _ = Definition(child);
        return relationships.Where(x => x.Child == child).Select(x => x.Parent).ToArray();
    }
    public IReadOnlyList<RequirementLevel> DownstreamLevels(RequirementLevel source)
    {
        _ = Definition(source);
        return relationships.Where(x => x.Parent == source).Select(x => x.Child).ToArray();
    }
    public TestProcedureLevel ProcedureLevel(RequirementLevel level) => Definition(level).Verification?.ProcedureLevel
        ?? throw new DomainException($"The {level} definition has no verification binding.");
    public VerificationArtifactProfile VerificationProfile(RequirementLevel level) => Definition(level).VerificationProfile
        ?? throw new DomainException($"The {level} definition has no verification profile.");
    public VerificationArtifactDefinition VerificationArtifact(VerificationArtifactKey key) =>
        VerificationArtifactVocabulary.Definition(key);
    public string ArtifactPrefix(VerificationArtifactKey key) => VerificationArtifact(key).ArtifactPrefix;
    public string TestChangeRequestPrefix(VerificationArtifactKey key) => VerificationArtifact(key).TestChangeRequestPrefix;
    public ReviewSubject WorkflowSubject(VerificationArtifactKey key) => VerificationArtifact(key).ReviewSubject;
    public ControlledDocumentType ControlledDocument(VerificationArtifactKey key) => VerificationArtifact(key).ControlledDocumentType;
    public RequirementLevel AssessmentTarget(VerificationArtifactKey key) => VerificationArtifact(key).AssessmentTarget;
    public VerificationArtifactKey ExecutableArtifactKey(RequirementLevel level) => VerificationProfile(level).ExecutableKey;
    public RequirementLevel RequirementLevelFor(TestProcedureLevel level) => definitions.SingleOrDefault(x => x.Verification?.ProcedureLevel == level)?.Level
        ?? throw new DomainException($"The project ladder does not configure verification level {level}.");
    public TestChangeReviewDiscipline Discipline(RequirementLevel level) => Definition(level).Verification?.Discipline
        ?? throw new DomainException($"The {level} definition has no verification binding.");
    public RequirementLevel RequirementLevelFor(TestChangeReviewDiscipline discipline) => definitions.SingleOrDefault(x => x.Verification?.Discipline == discipline)?.Level
        ?? throw new DomainException($"The project ladder does not configure review discipline {discipline}.");
    public ControlledDocumentType RequirementsDocument(RequirementLevel level) => Definition(level).RequirementsDocumentType
        ?? throw new DomainException($"The {level} definition has no requirements document.");
    public ControlledDocumentType TestProcedureDocument(RequirementLevel level) => VerificationProfile(level).ExecutableArtifact.DocumentType;
    public string TestProcedureDocumentTitle(RequirementLevel level) => Definition(level).TestProcedureDocumentTitle
        ?? throw new DomainException($"The {level} definition has no verification document title.");
    public string ControlledDocumentPrefix(ControlledDocumentType type) => catalogue.ControlledDocumentPrefix(type);
    public string ControlledDocumentTitle(ControlledDocumentType type) => catalogue.ControlledDocumentTitle(type);
    public string RequirementPrefix(RequirementLevel level) => Definition(level).RequirementPrefix;
    public string ChangeRequestPrefix(ChangeRequestType type, RequirementLevel? softwareLevel)
    {
        if (type == ChangeRequestType.System)
            return Definition(RequirementLevel.System).ChangeRequest?.Prefix
                ?? throw new DomainException("The project ladder does not configure System change control.");
        if (type == ChangeRequestType.Interface)
            return Definition(RequirementLevel.Interface).ChangeRequest?.Prefix
                ?? throw new DomainException("The project ladder does not configure Interface change control.");
        if (type != ChangeRequestType.Software || softwareLevel is null)
            throw new DomainException("A software change request must declare HLR or LLR scope before it can be numbered.");
        return Definition(softwareLevel.Value).ChangeRequest?.Prefix
            ?? throw new DomainException($"The project ladder does not configure {softwareLevel.Value} change control.");
    }
    public bool IsChangeRequestScopeValid(ChangeRequestType type, RequirementLevel? softwareLevel)
    {
        if (type == ChangeRequestType.System)
            return softwareLevel is null && definitions.Any(x => x.Level == RequirementLevel.System
                && x.ChangeRequest?.Type == ChangeRequestType.System && x.Has(LevelCapabilities.HasChangeControl));
        if (type == ChangeRequestType.Interface)
            return softwareLevel is null && definitions.Any(x => x.Level == RequirementLevel.Interface
                && x.ChangeRequest?.Type == ChangeRequestType.Interface && x.Has(LevelCapabilities.HasChangeControl));
        if (type != ChangeRequestType.Software || softwareLevel is null) return false;
        var binding = definitions.SingleOrDefault(x => x.Level == softwareLevel)?.ChangeRequest;
        return binding?.Type == ChangeRequestType.Software && binding.SoftwareLevel == softwareLevel
            && definitions.Any(x => x.Level == softwareLevel && x.Has(LevelCapabilities.HasChangeControl));
    }
    public bool AcceptsChangeRequest(ChangeRequestType type, RequirementLevel? softwareLevel, RequirementLevel level) =>
        IsChangeRequestScopeValid(type, softwareLevel) && AcceptsChangeRequest(type, level)
        && (type != ChangeRequestType.Software || softwareLevel == level);
    public string TestProcedurePrefix(TestProcedureLevel level) => definitions.SingleOrDefault(x => x.Verification?.ProcedureLevel == level)?.VerificationProfile?.ExecutableArtifact.ArtifactPrefix
        ?? throw new DomainException($"The project ladder does not configure verification level {level}.");
    public bool IsKnownTestProcedurePrefix(string baseNumber) => !string.IsNullOrWhiteSpace(baseNumber)
        && definitions.Any(x => x.VerificationProfile is not null
            && baseNumber.StartsWith(x.VerificationProfile.ExecutableArtifact.ArtifactPrefix + "-", StringComparison.OrdinalIgnoreCase));
    public string TestChangeReviewPrefix(TestChangeReviewDiscipline discipline)
    {
        _ = RequirementLevelFor(discipline);
        return definitions.SingleOrDefault(x => x.Verification?.Discipline == discipline)?.VerificationProfile?.ExecutableArtifact.TestChangeRequestPrefix
            ?? throw new DomainException($"The project ladder does not configure review discipline {discipline}.");
    }
    public ReviewSubject WorkflowSubject(ChangeRequestType type)
    {
        if (!definitions.Any(x => x.Has(LevelCapabilities.HasChangeControl) && x.ChangeRequest?.Type == type))
            throw new DomainException($"The project ladder does not configure {type} change-control workflow.");
        return catalogue.WorkflowSubject(type);
    }
    public ReviewSubject WorkflowSubject(TestChangeReviewDiscipline discipline)
    {
        _ = RequirementLevelFor(discipline);
        return definitions.SingleOrDefault(x => x.Verification?.Discipline == discipline)?.VerificationProfile?.ExecutableArtifact.ReviewSubject
            ?? throw new DomainException($"The project ladder does not configure review discipline {discipline}.");
    }
    public RequirementLevel ParseImportedRequirementLevel(string? value)
    {
        if (TryParseRequirementLevel(value, out var level)) return level;
        // ReqIF's established import contract treats absent and unrecognised level text as System. Keep that
        // compatibility fallback when System is present in the configured catalogue; a project that removes
        // System must fail closed instead of manufacturing a level outside its policy.
        if (definitions.Any(x => x.Level == RequirementLevel.System)) return RequirementLevel.System;
        throw new DomainException("The imported requirement does not name a configured ladder level.");
    }
    public bool TryParseRequirementLevel(string? value, out RequirementLevel level)
    {
        if (!catalogue.TryParseRequirementLevel(value, out level) || !levels.Contains(level))
        {
            level = (RequirementLevel)(-1);
            return false;
        }
        return true;
    }
    public bool AcceptsChangeRequest(ChangeRequestType type, RequirementLevel level)
    {
        var definition = Definition(level);
        return definition.Has(LevelCapabilities.HasChangeControl)
            && definition.ChangeRequest?.Type == type;
    }
    public bool IsDownstreamTarget(RequirementLevel level) => Definition(level) is not null && relationships.Any(x => x.Child == level);
    public bool HasCodeTraceability(RequirementLevel level) => Definition(level).Has(LevelCapabilities.HasCodeTraceability);
}

/// <summary>
/// Stored legacy projects compile their actual persisted graph while retaining the #702 compatibility contract
/// for generic trace mutation. A graph classified LegacyDefault but not equal to the legacy catalogue is rejected
/// by the infrastructure resolver rather than receiving this marker accidentally.
/// </summary>
public sealed class StoredLegacyProjectLadderPolicy : ResolvedProjectLadderPolicy, ILegacyLadderCompatibilityPolicy
{
    public StoredLegacyProjectLadderPolicy(ResolvedProjectLadder resolved, ILadderPolicy? catalogue = null)
        : base(resolved, catalogue) { }
}

/// <summary>One project-aware policy seam for stored/default, draft, and active project configurations.</summary>
public interface IProjectLadderPolicyResolver
{
    Task<ILadderPolicy> ResolveAsync(Guid projectId, CancellationToken ct = default);
}
