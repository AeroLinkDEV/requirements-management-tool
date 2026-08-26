using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

/// <summary>
/// The category vocabulary that replaced the four-kind Type, and the two rules that make it mean anything:
/// a report cannot reach the SCCB unclassified, and a category the migration assigned is distinguishable
/// from one a person chose.
/// </summary>
public sealed class ProblemReportCategoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    private static ProblemReport NewReport(ProblemReportCategory? category = null) =>
        new(Guid.NewGuid(), "PR-00001", "Disconnect tone is late", "The tone follows the disconnect.",
            "", "verification.engineer", Now, category: category);

    [Fact]
    public void Every_category_has_exactly_one_definition_and_one_code()
    {
        var defined = ProblemReportCategoryVocabulary.Definitions.Select(x => x.Category).ToArray();

        Assert.Equal(Enum.GetValues<ProblemReportCategory>(), defined);
        Assert.Equal(defined.Length, ProblemReportCategoryVocabulary.Definitions.Select(x => x.Code).Distinct().Count());
        Assert.All(ProblemReportCategoryVocabulary.Definitions, definition =>
        {
            Assert.Matches("^[1-9][0-9]$", definition.Code);
            Assert.False(string.IsNullOrWhiteSpace(definition.Label));
            Assert.False(string.IsNullOrWhiteSpace(definition.Meaning));
        });
    }

    /// <summary>
    /// The tens digit is the family, and that is the only grouping in the vocabulary that carries meaning.
    /// Two categories sharing a leading digit must therefore share a family, or the queue's family filter
    /// would return a set the codes do not describe.
    /// </summary>
    [Fact]
    public void The_leading_digit_of_a_code_is_its_family()
    {
        foreach (var group in ProblemReportCategoryVocabulary.Definitions.GroupBy(x => x.Code[0]))
            Assert.Single(group.Select(definition => definition.Family).Distinct());
    }

    [Theory]
    [InlineData("31", ProblemReportCategory.CodeFunctional)]
    [InlineData("62", ProblemReportCategory.EnvironmentTooling)]
    [InlineData("CodeNonFunctional", ProblemReportCategory.CodeNonFunctional)]
    [InlineData("testblocking", ProblemReportCategory.TestBlocking)]
    public void A_category_resolves_from_either_its_code_or_its_name(string value, ProblemReportCategory expected)
    {
        Assert.True(ProblemReportCategoryVocabulary.TryParse(value, out var category));
        Assert.Equal(expected, category);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("99")]
    [InlineData("Other")]
    [InlineData("Code")]
    public void A_value_outside_the_vocabulary_does_not_resolve(string? value)
    {
        // "Other" and "Code" are the retired kinds. They must not quietly resolve to anything: a stored
        // filter or an import carrying one is stale, and answering it with a guess is how a wrong category
        // gets written back.
        Assert.False(ProblemReportCategoryVocabulary.TryParse(value, out _));
    }

    /// <summary>Mirrors the SQL in ReplaceProblemReportTypeWithCategory, which is qualified separately.</summary>
    [Theory]
    [InlineData("Documentation", ProblemReportCategory.RequirementsDocumentation)]
    [InlineData("Code", ProblemReportCategory.CodeFunctional)]
    [InlineData("Test", ProblemReportCategory.TestBlocking)]
    [InlineData("Other", ProblemReportCategory.TaskDriver)]
    [InlineData("", ProblemReportCategory.TaskDriver)]
    [InlineData(null, ProblemReportCategory.TaskDriver)]
    public void The_retired_kind_maps_the_way_the_migration_backfills(string? kind, ProblemReportCategory expected) =>
        Assert.Equal(expected, ProblemReportCategoryVocabulary.FromRetiredKind(kind));

    [Fact]
    public void A_Draft_may_be_unclassified_and_says_so()
    {
        var report = NewReport();

        Assert.Null(report.Category);
        Assert.Null(report.CategoryProvenance);
    }

    [Fact]
    public void An_unclassified_Draft_cannot_be_sent_to_the_SCCB()
    {
        var report = NewReport();

        var refusal = Assert.Throws<DomainException>(() =>
            report.TransitionTo(ProblemReportState.ReadyForSccb, "verification.engineer", null, Now));

        // The message names the field, because "invalid transition" tells nobody what to do next.
        Assert.Contains("category", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ProblemReportState.Draft, report.State);
    }

    [Fact]
    public void A_classified_Draft_reaches_the_SCCB()
    {
        var report = NewReport(ProblemReportCategory.CodeFunctional);

        report.TransitionTo(ProblemReportState.ReadyForSccb, "verification.engineer", null, Now);

        Assert.Equal(ProblemReportState.ReadyForSccb, report.State);
        Assert.Equal(ProblemReportCategoryProvenance.Selected, report.CategoryProvenance);
    }

    /// <summary>
    /// Rejection stays open to an unclassified Draft. Refusing to let somebody close a report they have
    /// already judged worthless, purely because they will not first say what kind of worthless it is,
    /// would strand it in the queue forever.
    /// </summary>
    [Fact]
    public void An_unclassified_Draft_can_still_be_rejected()
    {
        var report = NewReport();

        report.TransitionTo(ProblemReportState.Rejected, "quality.analyst", "Raised against the wrong product.", Now);

        Assert.Equal(ProblemReportState.Rejected, report.State);
        Assert.Null(report.Category);
    }

    [Fact]
    public void Choosing_a_category_replaces_a_migration_derived_one_permanently()
    {
        var report = NewReport(ProblemReportCategory.CodeFunctional);

        report.UpdateDetails("verification.engineer", report.Title, report.Problem, "", "", "", "", "", "", "", "{}",
            report.Severity, report.Priority, Now.AddMinutes(1), ProblemReportCategory.CodeNonFunctional, null);

        Assert.Equal(ProblemReportCategory.CodeNonFunctional, report.Category);
        Assert.Equal(ProblemReportCategoryProvenance.Selected, report.CategoryProvenance);
    }

    /// <summary>
    /// A check-in that does not carry a category leaves the existing one alone. The editor round-trips a
    /// whole working copy, and a field it did not send is not an instruction to erase a controlled value.
    /// </summary>
    [Fact]
    public void An_edit_that_omits_the_category_keeps_the_one_already_recorded()
    {
        var report = NewReport(ProblemReportCategory.TestBlocking);

        report.UpdateDetails("verification.engineer", report.Title, report.Problem, "", "", "", "", "", "", "", "{}",
            report.Severity, report.Priority, Now.AddMinutes(1), category: null, workaround: null);

        Assert.Equal(ProblemReportCategory.TestBlocking, report.Category);
    }
}
