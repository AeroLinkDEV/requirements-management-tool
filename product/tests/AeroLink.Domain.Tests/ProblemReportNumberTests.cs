using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class ProblemReportNumberTests
{
    [Theory]
    [InlineData("PR-00001", 1)]
    [InlineData("PR-99999", 99999)]
    [InlineData("PR-100000", 100000)]
    [InlineData("RETAINED-NONNUMERIC", 1)]
    public void Numeric_suffix_order_preserves_current_and_retained_identifier_semantics(
        string reportNumber, int expected)
    {
        Assert.Equal(expected, ProblemReportNumber.Sequence(reportNumber));
        var report = new ProblemReport(Guid.NewGuid(), reportNumber, "Paging", "Paging proof", "",
            "engineer", DateTimeOffset.UtcNow);
        Assert.Equal(expected, report.NumberSequence);
    }
}
