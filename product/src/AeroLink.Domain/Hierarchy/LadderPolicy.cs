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
    ControlledDocumentType DocumentType);

/// <summary>The fixed enterprise schema/specification projection for a requirement level.</summary>
public sealed record RequirementsCatalogueBinding(string SchemaKey, string SchemaName, string SpecificationNumber,
    string SpecificationTitle);

/// <summary>A complete product-curated definition for one requirement level.</summary>
public sealed class LevelDefinition
{
    public LevelDefinition(RequirementLevel level, string requirementPrefix, LevelCapabilities capabilities,
        ChangeRequestLevelBinding? changeRequest = null, VerificationLevelBinding? verification = null,
        ControlledDocumentType? requirementsDocumentType = null, RequirementsCatalogueBinding? requirementsCatalogue = null,
        string? testProcedureDocumentTitle = null)
    {
        if (!Enum.IsDefined(level)) throw new DomainException($"Unknown requirement level value: {(int)level}.");
        if (string.IsNullOrWhiteSpace(requirementPrefix)) throw new DomainException("A level requires a requirement prefix.");

        Level = level;
        RequirementPrefix = requirementPrefix.Trim().ToUpperInvariant();
        Capabilities = capabilities;
        ChangeRequest = changeRequest;
        Verification = verification;
        RequirementsDocumentType = requirementsDocumentType;
        RequirementsCatalogue = requirementsCatalogue;
        TestProcedureDocumentTitle = string.IsNullOrWhiteSpace(testProcedureDocumentTitle)
            ? null
            : testProcedureDocumentTitle.Trim();

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
            "A disabled verification capability cannot carry a test-procedure document title.");

        if (changeRequest is not null)
        {
            if (string.IsNullOrWhiteSpace(changeRequest.Prefix))
                throw new DomainException("A change-request binding requires a prefix.");
            var expected = level == RequirementLevel.System
                ? (ChangeRequestType.System, (RequirementLevel?)null)
                : (ChangeRequestType.Software, level);
            if (changeRequest.Type != expected.Item1 || changeRequest.SoftwareLevel != expected.Item2)
                throw new DomainException($"The {level} change-request binding does not match its level.");
        }

        if (requirementsDocumentType is not null
            && requirementsDocumentType is not (ControlledDocumentType.Sysrd or ControlledDocumentType.SwrdHighLevel or ControlledDocumentType.SwrdLowLevel))
            throw new DomainException($"The {level} requirements-document binding must name a requirements document.");

        if (verification is not null)
        {
            if (string.IsNullOrWhiteSpace(verification.ProcedurePrefix))
                throw new DomainException("A verification binding requires a procedure prefix.");
            if (ProcedureLevelFor(level) != verification.ProcedureLevel
                || DisciplineFor(level) != verification.Discipline
                || TestDocumentFor(level) != verification.DocumentType)
                throw new DomainException($"The {level} verification binding does not match its level.");
        }
    }

    public RequirementLevel Level { get; }
    public string RequirementPrefix { get; }
    public LevelCapabilities Capabilities { get; }
    public ChangeRequestLevelBinding? ChangeRequest { get; }
    public VerificationLevelBinding? Verification { get; }
    public ControlledDocumentType? RequirementsDocumentType { get; }
    public RequirementsCatalogueBinding? RequirementsCatalogue { get; }
    public string? TestProcedureDocumentTitle { get; }

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
        RequirementLevel.HighLevel => ControlledDocumentType.HighLevelTestProcedures,
        RequirementLevel.LowLevel => ControlledDocumentType.LowLevelTestProcedures,
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
            new(TestProcedureLevel.HighLevel, TestChangeReviewDiscipline.HighLevelSoftware, "HLRTP", ControlledDocumentType.HighLevelTestProcedures),
            ControlledDocumentType.SwrdHighLevel,
            new("HLR", "High-Level Software Requirement", "HLRD-000001", "High-Level Software Requirements Document"),
            "High-Level Software Test Procedures Document"),
        new(RequirementLevel.LowLevel, "LLR", LevelCapabilities.HasChangeControl | LevelCapabilities.HasVerification | LevelCapabilities.HasRequirementsDocument | LevelCapabilities.HasCodeTraceability,
            new(ChangeRequestType.Software, RequirementLevel.LowLevel, "LLRCR"),
            new(TestProcedureLevel.LowLevel, TestChangeReviewDiscipline.LowLevelSoftware, "LLRTP", ControlledDocumentType.LowLevelTestProcedures),
            ControlledDocumentType.SwrdLowLevel,
            new("LLR", "Low-Level Software Requirement", "LLRD-000001", "Low-Level Software Requirements Document"),
            "Low-Level Software Test Procedures Document"),
    ];
    private static readonly IReadOnlyList<ControlledDocumentType> Documents =
    [
        ControlledDocumentType.Sysrd, ControlledDocumentType.SwrdHighLevel, ControlledDocumentType.SwrdLowLevel,
        ControlledDocumentType.SystemTestProcedures, ControlledDocumentType.HighLevelTestProcedures,
        ControlledDocumentType.LowLevelTestProcedures,
    ];

    public IReadOnlyList<RequirementLevel> OrderedLevels => Levels;
    public IReadOnlyList<LevelDefinition> Definitions => LevelDefinitions;
    public IReadOnlyList<LevelRelationship> ParentRelationships => Relationships;
    public IReadOnlyList<ControlledDocumentType> ControlledDocumentTypes => Documents;

    public LevelDefinition Definition(RequirementLevel level) => LevelDefinitions.SingleOrDefault(x => x.Level == level)
        ?? throw Unknown(level);

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

    public ControlledDocumentType TestProcedureDocument(RequirementLevel level) => Definition(level).Verification?.DocumentType
        ?? throw new DomainException($"The {level} definition has no test-procedure document.");

    public string TestProcedureDocumentTitle(RequirementLevel level) => Definition(level).TestProcedureDocumentTitle
        ?? throw new DomainException($"The {level} definition has no test-procedure document title.");

    public string ControlledDocumentPrefix(ControlledDocumentType type) => type switch
    {
        ControlledDocumentType.Sysrd => "SYSRD",
        ControlledDocumentType.SwrdHighLevel => "HLRD",
        ControlledDocumentType.SwrdLowLevel => "LLRD",
        ControlledDocumentType.SystemTestProcedures => "SYSTD",
        ControlledDocumentType.HighLevelTestProcedures => "HLRTD",
        ControlledDocumentType.LowLevelTestProcedures => "LLRTD",
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

    public string TestProcedurePrefix(TestProcedureLevel level) => level switch
    {
        TestProcedureLevel.System => Definition(RequirementLevel.System).Verification!.ProcedurePrefix,
        TestProcedureLevel.HighLevel => Definition(RequirementLevel.HighLevel).Verification!.ProcedurePrefix,
        TestProcedureLevel.LowLevel => Definition(RequirementLevel.LowLevel).Verification!.ProcedurePrefix,
        _ => throw Unknown(level),
    };

    public bool IsKnownTestProcedurePrefix(string baseNumber) =>
        !string.IsNullOrWhiteSpace(baseNumber)
        && OrderedLevels.Any(level => baseNumber.StartsWith(
            TestProcedurePrefix(ProcedureLevel(level)) + "-", StringComparison.OrdinalIgnoreCase));

    public string TestChangeReviewPrefix(TestChangeReviewDiscipline discipline) => discipline switch
    {
        TestChangeReviewDiscipline.System => "SYSTCR",
        TestChangeReviewDiscipline.HighLevelSoftware => "HLRTCR",
        TestChangeReviewDiscipline.LowLevelSoftware => "LLRTCR",
        _ => throw Unknown(discipline),
    };

    public ReviewSubject WorkflowSubject(ChangeRequestType type) => type switch
    {
        ChangeRequestType.System => ReviewSubject.System,
        ChangeRequestType.Software => ReviewSubject.Software,
        _ => throw Unknown(type),
    };

    public ReviewSubject WorkflowSubject(TestChangeReviewDiscipline discipline) => discipline switch
    {
        TestChangeReviewDiscipline.System => ReviewSubject.SystemTest,
        TestChangeReviewDiscipline.HighLevelSoftware => ReviewSubject.HighLevelSoftwareTest,
        TestChangeReviewDiscipline.LowLevelSoftware => ReviewSubject.LowLevelSoftwareTest,
        _ => throw Unknown(discipline),
    };

    public RequirementLevel ParseImportedRequirementLevel(string? value)
    {
        if (string.Equals(value?.Trim(), nameof(RequirementLevel.HighLevel), StringComparison.Ordinal))
            return RequirementLevel.HighLevel;
        if (string.Equals(value?.Trim(), nameof(RequirementLevel.LowLevel), StringComparison.Ordinal))
            return RequirementLevel.LowLevel;
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
            _ => (RequirementLevel)(-1),
        };
        return (int)level >= 0;
    }

    public bool AcceptsChangeRequest(ChangeRequestType type, RequirementLevel level)
    {
        _ = Definition(level);
        return type switch
        {
            ChangeRequestType.System => level == RequirementLevel.System,
            ChangeRequestType.Software => level is RequirementLevel.HighLevel or RequirementLevel.LowLevel,
            _ => throw Unknown(type),
        };
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
/// This type is deliberately an injected/test policy until activation is made authoritative by #706.
/// </summary>
public sealed class ResolvedProjectLadderPolicy : ILadderPolicy
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
            return new LevelDefinition(x.Level, source.RequirementPrefix, capabilities,
                capabilities.HasFlag(LevelCapabilities.HasChangeControl) ? source.ChangeRequest : null,
                capabilities.HasFlag(LevelCapabilities.HasVerification) ? source.Verification : null,
                capabilities.HasFlag(LevelCapabilities.HasRequirementsDocument) ? source.RequirementsDocumentType : null,
                capabilities.HasFlag(LevelCapabilities.HasRequirementsDocument) ? source.RequirementsCatalogue : null,
                capabilities.HasFlag(LevelCapabilities.HasVerification) ? source.TestProcedureDocumentTitle : null);
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
    public RequirementLevel RequirementLevelFor(TestProcedureLevel level) => definitions.SingleOrDefault(x => x.Verification?.ProcedureLevel == level)?.Level
        ?? throw new DomainException($"The project ladder does not configure procedure level {level}.");
    public TestChangeReviewDiscipline Discipline(RequirementLevel level) => Definition(level).Verification?.Discipline
        ?? throw new DomainException($"The {level} definition has no verification binding.");
    public RequirementLevel RequirementLevelFor(TestChangeReviewDiscipline discipline) => definitions.SingleOrDefault(x => x.Verification?.Discipline == discipline)?.Level
        ?? throw new DomainException($"The project ladder does not configure review discipline {discipline}.");
    public ControlledDocumentType RequirementsDocument(RequirementLevel level) => Definition(level).RequirementsDocumentType
        ?? throw new DomainException($"The {level} definition has no requirements document.");
    public ControlledDocumentType TestProcedureDocument(RequirementLevel level) => Definition(level).Verification?.DocumentType
        ?? throw new DomainException($"The {level} definition has no test-procedure document.");
    public string TestProcedureDocumentTitle(RequirementLevel level) => Definition(level).TestProcedureDocumentTitle
        ?? throw new DomainException($"The {level} definition has no test-procedure document title.");
    public string ControlledDocumentPrefix(ControlledDocumentType type) => catalogue.ControlledDocumentPrefix(type);
    public string ControlledDocumentTitle(ControlledDocumentType type) => catalogue.ControlledDocumentTitle(type);
    public string RequirementPrefix(RequirementLevel level) => Definition(level).RequirementPrefix;
    public string ChangeRequestPrefix(ChangeRequestType type, RequirementLevel? softwareLevel)
    {
        if (type == ChangeRequestType.System)
            return Definition(RequirementLevel.System).ChangeRequest?.Prefix
                ?? throw new DomainException("The project ladder does not configure System change control.");
        if (type != ChangeRequestType.Software || softwareLevel is null)
            throw new DomainException("A software change request must declare a configured requirement level before it can be numbered.");
        return Definition(softwareLevel.Value).ChangeRequest?.Prefix
            ?? throw new DomainException($"The project ladder does not configure {softwareLevel.Value} change control.");
    }
    public bool IsChangeRequestScopeValid(ChangeRequestType type, RequirementLevel? softwareLevel)
    {
        if (type == ChangeRequestType.System)
            return softwareLevel is null && definitions.Any(x => x.Level == RequirementLevel.System
                && x.ChangeRequest?.Type == ChangeRequestType.System && x.Has(LevelCapabilities.HasChangeControl));
        if (type != ChangeRequestType.Software || softwareLevel is null) return false;
        var binding = definitions.SingleOrDefault(x => x.Level == softwareLevel)?.ChangeRequest;
        return binding?.Type == ChangeRequestType.Software && binding.SoftwareLevel == softwareLevel
            && definitions.Any(x => x.Level == softwareLevel && x.Has(LevelCapabilities.HasChangeControl));
    }
    public bool AcceptsChangeRequest(ChangeRequestType type, RequirementLevel? softwareLevel, RequirementLevel level) =>
        IsChangeRequestScopeValid(type, softwareLevel) && AcceptsChangeRequest(type, level)
        && (type != ChangeRequestType.Software || softwareLevel == level);
    public string TestProcedurePrefix(TestProcedureLevel level) => definitions.SingleOrDefault(x => x.Verification?.ProcedureLevel == level)?.Verification?.ProcedurePrefix
        ?? throw new DomainException($"The project ladder does not configure procedure level {level}.");
    public bool IsKnownTestProcedurePrefix(string baseNumber) => !string.IsNullOrWhiteSpace(baseNumber)
        && definitions.Any(x => x.Verification is not null && baseNumber.StartsWith(x.Verification.ProcedurePrefix + "-", StringComparison.OrdinalIgnoreCase));
    public string TestChangeReviewPrefix(TestChangeReviewDiscipline discipline) => catalogue.TestChangeReviewPrefix(discipline);
    public ReviewSubject WorkflowSubject(ChangeRequestType type) => catalogue.WorkflowSubject(type);
    public ReviewSubject WorkflowSubject(TestChangeReviewDiscipline discipline) => catalogue.WorkflowSubject(discipline);
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
        return type switch
        {
            ChangeRequestType.System => level == RequirementLevel.System && definition.Has(LevelCapabilities.HasChangeControl),
            ChangeRequestType.Software => level != RequirementLevel.System && definition.Has(LevelCapabilities.HasChangeControl),
            _ => false,
        };
    }
    public bool IsDownstreamTarget(RequirementLevel level) => Definition(level) is not null && relationships.Any(x => x.Child == level);
    public bool HasCodeTraceability(RequirementLevel level) => Definition(level).Has(LevelCapabilities.HasCodeTraceability);
}

/// <summary>One project-aware policy seam. Runtime resolution remains legacy-only until #706 activation.</summary>
public interface IProjectLadderPolicyResolver
{
    Task<ILadderPolicy> ResolveAsync(Guid projectId, CancellationToken ct = default);
}
