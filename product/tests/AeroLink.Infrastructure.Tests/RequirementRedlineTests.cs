using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class RequirementRedlineTests
{
    [Theory]
    [InlineData(399)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(10000)]
    public void A_changed_tail_is_never_silently_omitted(int changedToken)
    {
        var prefix = string.Join(" ", Enumerable.Repeat("requirement", changedToken - 1));
        var before = prefix + " reject";
        var after = prefix + " accept";
        var result = EnterpriseRequirementsService.Diff(before, after);
        Assert.True(result.IsComplete);
        Assert.Contains(result.Spans, x => x.Kind == "removed" && x.Text.EndsWith("reject"));
        Assert.Contains(result.Spans, x => x.Kind == "added" && x.Text.EndsWith("accept"));
        Assert.Equal(changedToken > 400 ? "WholeField" : "Detailed", result.Mode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Long_additions_and_removals_retain_complete_original_text(bool addition)
    {
        var text = string.Join("\n", Enumerable.Repeat("The FMS shall retain sûreté and Ω.", 1000));
        var result = EnterpriseRequirementsService.Diff(addition ? "" : text, addition ? text : "");
        Assert.Equal("WholeField", result.Mode);
        Assert.Equal(text, Assert.Single(result.Spans, x => x.Kind == (addition ? "added" : "removed")).Text);
    }

    [Fact]
    public void Identical_long_text_remains_complete_and_unchanged()
    {
        var text = string.Join("\t", Enumerable.Repeat("same", 10000));
        var result = EnterpriseRequirementsService.Diff(text, text);
        var span = Assert.Single(result.Spans);
        Assert.Equal("same", span.Kind);
        Assert.Equal(text, span.Text);
    }

    [Fact]
    public void Repeated_words_keep_both_sides_of_a_short_change()
    {
        var result = EnterpriseRequirementsService.Diff("shall shall reject shall", "shall accept shall shall");
        Assert.Equal("shall shall reject shall", string.Join(" ", result.Spans.Where(x => x.Kind != "added").Select(x => x.Text)));
        Assert.Equal("shall accept shall shall", string.Join(" ", result.Spans.Where(x => x.Kind != "removed").Select(x => x.Text)));
    }

    [Fact]
    public void Cancellation_does_not_return_an_apparently_complete_comparison()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => EnterpriseRequirementsService.Diff("before", "after", cancellation.Token));
    }
}
