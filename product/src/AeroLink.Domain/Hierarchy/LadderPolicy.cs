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

/// <summary>The current legacy/default composition of the ladder policy.</summary>
public sealed class LegacyLadderPolicy : ILadderPolicy
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
