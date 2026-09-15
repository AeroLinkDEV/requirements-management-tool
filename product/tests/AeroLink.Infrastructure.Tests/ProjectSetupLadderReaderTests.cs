using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Direct coverage for the JSON-reading boundary the finalizer now shares. A step position is authoring
/// input, and the maintained contract never replaced an unreadable position with a valid-looking one: the
/// prior typed payload read a missing position as 0 (refused by the validator) and a null or fractional one
/// as a payload refusal. This reader keeps that fail-closed behaviour and names the problem instead of
/// silently inventing a row index.
/// </summary>
public sealed class ProjectSetupLadderReaderTests
{
    private const string StepWithoutPosition =
        """{"catalogueEntry":"System","capabilities":7,"enabledArtifactKinds":["Procedure"]}""";
    private const string NullPosition =
        """{"catalogueEntry":"System","position":null,"capabilities":7,"enabledArtifactKinds":["Procedure"]}""";
    private const string FractionalPosition =
        """{"catalogueEntry":"System","position":1.5,"capabilities":7,"enabledArtifactKinds":["Procedure"]}""";
    private const string TextPosition =
        """{"catalogueEntry":"System","position":"first","capabilities":7,"enabledArtifactKinds":["Procedure"]}""";

    private static string Ladder(string step) =>
        $$"""{"steps":[{{step}}],"relationships":[]}""";

    [Theory]
    [InlineData(StepWithoutPosition, "ladder_position_missing")]
    [InlineData(NullPosition, "ladder_position_missing")]
    [InlineData(FractionalPosition, "ladder_position_unreadable")]
    [InlineData(TextPosition, "ladder_position_unreadable")]
    public void An_unreadable_position_is_reported_and_never_defaulted(string step, string expectedCode)
    {
        var reading = ProjectSetupLadderReader.Read(Ladder(step));

        var parsed = Assert.Single(reading.Steps);
        // Zero, not the row index: the step stays visible for repair and the validator still refuses it.
        Assert.Equal(0, parsed.Position);
        var finding = Assert.Single(reading.Findings);
        Assert.Equal(expectedCode, finding.Code);
        Assert.Equal("System", finding.Level);
        Assert.Equal("position", finding.Field);
    }

    [Fact]
    public void Readable_positions_and_the_new_project_default_are_unchanged()
    {
        var reading = ProjectSetupLadderReader.Read(Ladder(
            """{"catalogueEntry":"System","position":2,"capabilities":7,"enabledArtifactKinds":["Procedure"]}"""));

        Assert.Empty(reading.Findings);
        Assert.Equal(2, Assert.Single(reading.Steps).Position);

        var newProjectDefault = ProjectSetupLadderReader.Read("{}");
        Assert.True(newProjectDefault.IsDefault);
        Assert.Empty(newProjectDefault.Findings);
    }
}
