using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

/// <summary>
/// Direct coverage for the validator the #1045 correction made shared: <see cref="ProjectLadderDraftValidator.Inspect"/>
/// must be exactly the non-throwing view of <see cref="ProjectLadderDraftValidator.Validate"/>, its findings must
/// identify the level and field at fault, and the canonical-history algorithms must stay byte-stable because
/// stored snapshots are evidence of the shape that wrote them.
/// </summary>
public sealed class ProjectLadderDraftValidatorTests
{
    private const LevelCapabilities SoftwareCapabilities = LevelCapabilities.HasChangeControl
        | LevelCapabilities.HasVerification | LevelCapabilities.HasRequirementsDocument;

    private static readonly LadderRelationshipDraft[] DefaultEdges =
        [new("System", "HighLevel"), new("HighLevel", "LowLevel")];

    private static IReadOnlyList<LadderStepDraft> CoherentSteps() =>
    [
        new("System", 1, SoftwareCapabilities, [VerificationArtifactKind.Procedure]),
        new("HighLevel", 2, SoftwareCapabilities, [VerificationArtifactKind.Case]),
        new("LowLevel", 3, SoftwareCapabilities, [VerificationArtifactKind.Case]),
    ];

    [Fact]
    public void A_coherent_ladder_produces_no_findings_and_validate_returns_the_same_steps()
    {
        var steps = CoherentSteps();

        Assert.Empty(ProjectLadderDraftValidator.Inspect(steps, DefaultEdges, LegacyLadderPolicy.Instance));
        var (validated, edges) = ProjectLadderDraftValidator.Validate(steps, DefaultEdges, LegacyLadderPolicy.Instance);
        Assert.Equal(steps, validated);
        Assert.Equal(DefaultEdges, edges);
    }

    [Fact]
    public void Validate_raises_exactly_the_first_finding_Inspect_reports()
    {
        var steps = new[]
        {
            // Verification is disabled while the level still enables Procedure: the recorded owner shape.
            new LadderStepDraft("System", 1, LevelCapabilities.HasChangeControl | LevelCapabilities.HasRequirementsDocument,
                [VerificationArtifactKind.Procedure]),
            new LadderStepDraft("HighLevel", 2, SoftwareCapabilities, [VerificationArtifactKind.Case]),
            new LadderStepDraft("LowLevel", 3, SoftwareCapabilities, [VerificationArtifactKind.Case]),
        };

        var findings = ProjectLadderDraftValidator.Inspect(steps, DefaultEdges, LegacyLadderPolicy.Instance);
        var finding = Assert.Single(findings);
        Assert.Equal("verification_disabled_with_artifacts", finding.Code);
        Assert.Equal("System", finding.Level);
        Assert.Equal("enabledArtifactKinds", finding.Field);

        var thrown = Assert.Throws<DomainException>(() =>
            ProjectLadderDraftValidator.Validate(steps, DefaultEdges, LegacyLadderPolicy.Instance));
        Assert.Equal(findings[0].Message, thrown.Message);
    }

    [Theory]
    [InlineData("no-steps")]
    [InlineData("unnamed-step")]
    [InlineData("duplicate-step")]
    [InlineData("position-gap")]
    [InlineData("unknown-level")]
    [InlineData("relationship-self")]
    [InlineData("relationship-duplicate")]
    [InlineData("relationship-unknown-endpoint")]
    [InlineData("relationship-direction")]
    public void Structural_problems_are_reported_and_thrown_consistently(string mutation)
    {
        var steps = CoherentSteps().ToList();
        var edges = DefaultEdges.ToList();
        switch (mutation)
        {
            case "no-steps":
                steps.Clear();
                edges.Clear();
                break;
            case "unnamed-step":
                steps[1] = steps[1] with { CatalogueEntry = "" };
                break;
            case "duplicate-step":
                steps[2] = steps[2] with { CatalogueEntry = "HighLevel" };
                break;
            case "position-gap":
                steps[2] = steps[2] with { Position = 7 };
                break;
            case "unknown-level":
                steps[1] = steps[1] with { CatalogueEntry = "Subsystem" };
                break;
            case "relationship-self":
                edges.Add(new LadderRelationshipDraft("System", "System"));
                break;
            case "relationship-duplicate":
                edges.Add(new LadderRelationshipDraft("System", "HighLevel"));
                break;
            case "relationship-unknown-endpoint":
                edges.Add(new LadderRelationshipDraft("System", "Customer"));
                break;
            case "relationship-direction":
                edges.Add(new LadderRelationshipDraft("LowLevel", "System"));
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation {mutation}.");
        }

        var findings = ProjectLadderDraftValidator.Inspect(steps, edges, LegacyLadderPolicy.Instance);
        Assert.NotEmpty(findings);
        var thrown = Assert.Throws<DomainException>(() =>
            ProjectLadderDraftValidator.Validate(steps, edges, LegacyLadderPolicy.Instance));
        Assert.Equal(findings[0].Message, thrown.Message);
    }

    [Fact]
    public void Verification_enabled_with_an_explicit_empty_profile_is_invalid_but_absent_keeps_the_legacy_fallback()
    {
        var explicitEmpty = CoherentSteps().ToList();
        explicitEmpty[1] = explicitEmpty[1] with { EnabledArtifactKinds = [] };
        var findings = ProjectLadderDraftValidator.Inspect(explicitEmpty, DefaultEdges, LegacyLadderPolicy.Instance);
        var finding = Assert.Single(findings);
        Assert.Equal("verification_profile_invalid", finding.Code);
        Assert.Equal("HighLevel", finding.Level);

        // Absent is a different saved fact: the maintained legacy interpretation is Case-only and is valid.
        var absent = CoherentSteps().ToList();
        absent[1] = absent[1] with { EnabledArtifactKinds = null };
        Assert.Empty(ProjectLadderDraftValidator.Inspect(absent, DefaultEdges, LegacyLadderPolicy.Instance));
        Assert.Equal([VerificationArtifactKind.Case], absent[1].EffectiveKinds(
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.HighLevel)));
    }

    [Fact]
    public void An_unrecognized_token_is_reported_with_its_level_and_a_bounded_token()
    {
        var steps = CoherentSteps().ToList();
        var token = new string('x', ProjectLadderDraftValidator.MaxDiagnosticTokenLength + 20);
        steps[1] = steps[1] with
        {
            EnabledArtifactKinds = [VerificationArtifactKind.Case],
            UnrecognizedArtifactKinds = [token],
        };

        var findings = ProjectLadderDraftValidator.Inspect(steps, DefaultEdges, LegacyLadderPolicy.Instance);
        var finding = Assert.Single(findings);
        Assert.Equal("artifact_kind_unrecognized", finding.Code);
        Assert.Equal("HighLevel", finding.Level);
        Assert.Equal(ProjectLadderDraftValidator.DiagnosticToken(token), finding.Token);
        Assert.Equal(ProjectLadderDraftValidator.MaxDiagnosticTokenLength + 1, finding.Token!.Length);
        Assert.Contains(finding.Token!, finding.Message);
        Assert.DoesNotContain(token, finding.Message);
    }

    [Fact]
    public void Stored_canonical_history_keeps_its_byte_for_byte_shape_and_schema_verification()
    {
        var steps = CoherentSteps().ToList();
        steps[1] = steps[1] with { EnabledArtifactKinds = null };

        // The v1 algorithm is evidence of what wrote a stored snapshot: it must not gain the v2 schema and
        // profile text, and its hash must stay exactly the recorded one.
        const string legacyCanonical = "steps[1:System:7;2:HighLevel:7;3:LowLevel:7]|edges[HighLevel>LowLevel;System>HighLevel]";
        const string legacyHash = "ac0414b297f0c3054cb33f4206961fd4f5e6dadb407c3705ca69add1db6080ec";
        Assert.Equal(legacyCanonical, ProjectLadderSnapshot.Canonicalize(steps, DefaultEdges));
        Assert.Equal(legacyHash, ProjectLadderSnapshot.Hash(legacyCanonical));
        Assert.True(ProjectLadderSnapshot.Verify(legacyCanonical, legacyHash,
            ProjectLadderSnapshot.LegacySchemaVersion));
        Assert.False(ProjectLadderSnapshot.Verify(legacyCanonical, legacyHash,
            ProjectLadderSnapshot.CurrentSchemaVersion));

        // The v2 algorithm records the effective profile, still without database-generated identities.
        var v2Canonical = ProjectLadderSnapshot.CanonicalizeV2(steps, DefaultEdges);
        Assert.StartsWith($"schema[{ProjectLadderSnapshot.CurrentSchemaVersion}]|", v2Canonical,
            StringComparison.Ordinal);
        Assert.Equal(v2Canonical, ProjectLadderSnapshot.CanonicalizeForSchema(
            ProjectLadderSnapshot.CurrentSchemaVersion, steps, DefaultEdges));
        Assert.Equal(ProjectLadderSnapshot.Hash(v2Canonical), ProjectLadderSnapshot.HashForSchema(
            ProjectLadderSnapshot.CurrentSchemaVersion, steps, DefaultEdges));
        Assert.True(ProjectLadderSnapshot.Verify(v2Canonical, ProjectLadderSnapshot.Hash(v2Canonical),
            ProjectLadderSnapshot.CurrentSchemaVersion));
        Assert.False(ProjectLadderSnapshot.Verify(v2Canonical, ProjectLadderSnapshot.Hash(v2Canonical),
            ProjectLadderSnapshot.LegacySchemaVersion));
    }

    [Fact]
    public void Canonical_v2_still_refuses_a_disabled_level_that_enables_artifacts()
    {
        var steps = CoherentSteps().ToList();
        steps[0] = steps[0] with
        {
            Capabilities = LevelCapabilities.HasChangeControl | LevelCapabilities.HasRequirementsDocument,
        };

        var thrown = Assert.Throws<DomainException>(() =>
            ProjectLadderSnapshot.CanonicalizeV2(steps, DefaultEdges));
        Assert.Contains("cannot enable verification artifacts", thrown.Message);
    }
}
