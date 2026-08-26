using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

/// <summary>
/// What kind of problem this is, in the vocabulary the people who triage them actually use.
///
/// This replaces <c>ProblemReportType</c>, which offered Documentation, Code, Test and Other. Those four
/// could not express the distinction that decides what happens next: whether a code defect is visible to
/// somebody flying the aircraft, and whether a test problem stops the testing. Both halves of each pair
/// were the same value, so the queue could not be narrowed to the work that had to move first.
///
/// Stored by name, so adding a category later is a code change and not a data migration — the same choice
/// the retired type made, and the reason there is no ordinal anywhere in the schema.
/// </summary>
public enum ProblemReportCategory
{
    TaskDriver,
    ProductImprovement,
    CodeFunctional,
    CodeNonFunctional,
    RequirementsDocumentation,
    TestBlocking,
    TestNonBlocking,
    DataConfiguration,
    EnvironmentTooling,
}

/// <summary>
/// Whether a person chose the category, or a migration assigned it.
///
/// The 2026-08 migration mapped every retained report from the four retired kinds onto the nine, which
/// means a report that only ever said "Code" now says "Code Issue — Functional Impact". Nobody made that
/// judgement: the old vocabulary could not express it. Recording how the value arrived keeps the record
/// honest about which categories are evidence and which are a starting point, the same way a release
/// waiver carries <c>LegacyUnverified</c> and a closure package carries its provenance.
/// </summary>
public enum ProblemReportCategoryProvenance
{
    /// <summary>A person chose this category on this record.</summary>
    Selected,

    /// <summary>Assigned by the category migration from a retired kind. Not a judgement anybody made.</summary>
    MigrationDerived,
}

/// <summary>
/// The controlled category vocabulary: fixed, defined in code, and identical on every Project.
///
/// Deliberately not a per-project catalogue like the requirement ladder. Problem Report numbers are
/// allocated repository-wide across every Project, so a category that meant different things in different
/// Projects would make the one genuinely cross-Project record incomparable with itself.
///
/// The two-digit code is part of the vocabulary rather than a presentation detail: it is what survives an
/// export, a generated document and a conversation with a supplier. Its tens digit is the family, which is
/// the only grouping in the vocabulary that carries meaning — 3x is always a code defect, 5x is always a
/// test problem — and the units digit separates the two halves that change what somebody does next.
/// </summary>
public static class ProblemReportCategoryVocabulary
{
    public sealed record Definition(
        ProblemReportCategory Category,
        string Code,
        string Family,
        string Label,
        string Meaning);

    private static readonly Definition[] DefinitionValues =
    [
        new(ProblemReportCategory.TaskDriver, "11", "Task", "Task Driver",
            "Work that needs to happen but is not correcting a known defect. Drives and initiates work, and "
            + "acts as a reminder for future implementation, follow-up, cleanup or investigation."),
        new(ProblemReportCategory.ProductImprovement, "21", "Improvement", "Product Improvement",
            "Existing behaviour meets requirements and intent, but there is a worthwhile improvement to "
            + "functionality, usability, maintainability, performance or architecture. Not an urgent fix, "
            + "because requirements are already being met, though other reasons may still drive implementing it."),
        new(ProblemReportCategory.CodeFunctional, "31", "Code", "Code Issue — Functional Impact",
            "Software or code defect that causes incorrect, missing or unintended externally observable "
            + "functionality."),
        new(ProblemReportCategory.CodeNonFunctional, "32", "Code", "Code Issue — Non-Functional Impact",
            "Code defect with no current functional failure: maintainability, robustness, performance, "
            + "logging, resource use, code quality, error handling, technical debt."),
        new(ProblemReportCategory.RequirementsDocumentation, "41", "Requirements", "Requirements / Documentation Issue",
            "Error, ambiguity, inconsistency, missing information, or a traceability or documentation problem, "
            + "where there is no identified implementation defect."),
        new(ProblemReportCategory.TestBlocking, "51", "Test", "Test Issue — Blocking",
            "Test, test infrastructure, procedure or data issue that prevents valid execution, completion or "
            + "meaningful review of testing."),
        new(ProblemReportCategory.TestNonBlocking, "52", "Test", "Test Issue — Non-Blocking",
            "Test-related defect or weakness that should be corrected but does not prevent valid execution "
            + "or review."),
        new(ProblemReportCategory.DataConfiguration, "61", "Environment", "Data / Configuration Issue",
            "Problem with configuration, reference data, permissions, templates, project setup, mappings, "
            + "imported data or migration data, rather than the application code itself."),
        new(ProblemReportCategory.EnvironmentTooling, "62", "Environment", "Environment / Tooling Issue",
            "Build, deployment, CI, Docker, scripts, developer environment, infrastructure, installation, "
            + "browser or environment compatibility."),
    ];

    public static IReadOnlyList<Definition> Definitions { get; } = Array.AsReadOnly(DefinitionValues);

    /// <summary>The families in code order, for grouping a picker or a queue filter.</summary>
    public static IReadOnlyList<string> Families { get; } =
        Array.AsReadOnly(DefinitionValues.Select(definition => definition.Family).Distinct().ToArray());

    public static Definition Of(ProblemReportCategory category) =>
        DefinitionValues.SingleOrDefault(definition => definition.Category == category)
        ?? throw new DomainException($"The Problem Report category '{category}' has no definition.");

    public static string Code(ProblemReportCategory category) => Of(category).Code;

    public static string Family(ProblemReportCategory category) => Of(category).Family;

    /// <summary>
    /// Resolves a category from either its name or its two-digit code, so a caller that has only the code —
    /// an import, an export being read back, a stored filter — does not have to know the enum spelling.
    /// </summary>
    public static bool TryParse(string? value, out ProblemReportCategory category)
    {
        var candidate = value?.Trim();
        if (!string.IsNullOrEmpty(candidate))
        {
            var byCode = DefinitionValues.SingleOrDefault(definition =>
                string.Equals(definition.Code, candidate, StringComparison.Ordinal));
            if (byCode is not null) { category = byCode.Category; return true; }
            if (Enum.TryParse<ProblemReportCategory>(candidate, ignoreCase: true, out var byName)
                && DefinitionValues.Any(definition => definition.Category == byName))
            {
                category = byName;
                return true;
            }
        }
        category = default;
        return false;
    }

    /// <summary>
    /// The category a retained report receives from the 2026-08 migration, given the kind it used to carry.
    ///
    /// Kept here rather than written into the migration's SQL so the mapping is testable and has one
    /// spelling. Every result is <see cref="ProblemReportCategoryProvenance.MigrationDerived"/>: mapping
    /// Code onto "functional impact" and Test onto "blocking" asserts a judgement the retired vocabulary
    /// could not hold, and the record has to be able to say so.
    /// </summary>
    public static ProblemReportCategory FromRetiredKind(string? retiredKind) => (retiredKind?.Trim()) switch
    {
        "Documentation" => ProblemReportCategory.RequirementsDocumentation,
        "Code" => ProblemReportCategory.CodeFunctional,
        "Test" => ProblemReportCategory.TestBlocking,
        _ => ProblemReportCategory.TaskDriver,
    };
}
