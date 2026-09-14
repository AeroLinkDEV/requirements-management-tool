using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProjectSetupReviewRulesTests
{
    [Theory]
    [InlineData("System")]
    [InlineData("HighLevel")]
    [InlineData("LowLevel")]
    [InlineData("Interface")]
    public void Disabled_capabilities_do_not_offer_unusable_review_workflows(string level)
    {
        var ladder = JsonSerializer.Serialize(new { steps = new[] { new {
            catalogueEntry = level, position = 1, capabilities = 0, enabledArtifactKinds = Array.Empty<string>()
        } }, relationships = Array.Empty<object>() });
        using var document = JsonDocument.Parse(ProjectSetupReviewRules.SuggestedJson(ladder, Guid.NewGuid()));
        Assert.Empty(document.RootElement.GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public void Maintained_standard_is_a_concrete_typed_definition_for_the_default_ladder()
    {
        var json = ProjectSetupReviewRules.SuggestedJson("{}", Guid.NewGuid());

        using var document = JsonDocument.Parse(json);
        var rules = document.RootElement.GetProperty("rules").EnumerateArray().ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            var stages = rule.GetProperty("stages").EnumerateArray().ToArray();
            Assert.Contains(stages, x => x.GetProperty("kind").GetString() == "Review");
            Assert.Contains(stages, x => x.GetProperty("kind").GetString() == "Approval");
            Assert.All(stages, stage => Assert.True(stage.TryGetProperty("authorityKind", out _)));
        });
    }

    [Fact]
    public void Interface_standard_preserves_the_maintained_configuration_manager_base_role()
    {
        const string ladder = """
            {
              "steps": [
                { "catalogueEntry": "Interface", "position": 1, "capabilities": "HasChangeControl", "enabledArtifactKinds": [] }
              ],
              "relationships": []
            }
            """;

        using var document = JsonDocument.Parse(ProjectSetupReviewRules.SuggestedJson(ladder, Guid.NewGuid()));
        var rule = Assert.Single(document.RootElement.GetProperty("rules").EnumerateArray());
        Assert.Equal(nameof(ReviewSubject.Interface), rule.GetProperty("subject").GetString());
        var engineeringReview = rule.GetProperty("stages").EnumerateArray().First();
        Assert.Equal(nameof(ProgramRole.ConfigurationManager), engineeringReview.GetProperty("requiredRole").GetString());
        Assert.Equal(nameof(ReviewStageAuthorityKind.BaseRole),
            engineeringReview.GetProperty("authorityKind").GetString());
    }

    [Fact]
    public void Customer_only_ladder_has_an_explicit_empty_rule_set()
    {
        const string ladder = """
            {
              "steps": [
                { "catalogueEntry": "Customer", "position": 1, "capabilities": "None", "enabledArtifactKinds": [] }
              ],
              "relationships": []
            }
            """;

        using var document = JsonDocument.Parse(ProjectSetupReviewRules.SuggestedJson(ladder, Guid.NewGuid()));
        Assert.Empty(document.RootElement.GetProperty("rules").EnumerateArray());
    }
}
