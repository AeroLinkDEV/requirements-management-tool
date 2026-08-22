using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

public sealed class VerificationProfileTests
{
    [Fact]
    public void Legacy_profiles_preserve_current_outputs_and_derive_executable_kind()
    {
        var system = LegacyLadderPolicy.Instance.VerificationProfile(RequirementLevel.System);
        var high = LegacyLadderPolicy.Instance.VerificationProfile(RequirementLevel.HighLevel);

        Assert.Equal([VerificationArtifactKind.Procedure], system.EnabledKinds);
        Assert.Equal(VerificationArtifactKind.Procedure, system.ExecutableArtifact.Kind);
        Assert.Equal([VerificationArtifactKind.Case], high.EnabledKinds);
        Assert.Equal(VerificationArtifactKind.Case, high.ExecutableArtifact.Kind);
        Assert.Equal("SYSTP", system.ExecutableArtifact.ArtifactPrefix);
        Assert.Equal("HLRTCR", high.ExecutableArtifact.TestChangeRequestPrefix);
        Assert.Equal(ControlledDocumentType.HighLevelTestProcedures, high.ExecutableArtifact.DocumentType);
    }

    [Theory]
    [InlineData(VerificationDiscipline.System, "Case")]
    [InlineData(VerificationDiscipline.HighLevelSoftware, "Procedure")]
    [InlineData(VerificationDiscipline.HighLevelSoftware, "Procedure,Case")]
    public void Invalid_profile_shapes_are_rejected(VerificationDiscipline discipline, string kinds)
    {
        var definitions = kinds.Split(',').Select(kind => new VerificationArtifactDefinition(
            new VerificationArtifactKey(discipline, Enum.Parse<VerificationArtifactKind>(kind)),
            "TEST", "TESTCR", ReviewSubject.SystemTest, ControlledDocumentType.SystemTestProcedures,
            RequirementLevel.System));

        Assert.Throws<DomainException>(() => new VerificationArtifactProfile(discipline, definitions));
    }

    [Fact]
    public void V1_snapshot_bytes_remain_stable_while_v2_carries_profile_shape()
    {
        var steps = new[] { new LadderStepDraft("System", 1,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities) };
        var edges = Array.Empty<LadderRelationshipDraft>();
        var v1 = ProjectLadderSnapshot.Canonicalize(steps, edges);
        Assert.Equal("steps[1:System:7]|edges[]", v1);
        Assert.True(ProjectLadderSnapshot.Verify(v1, ProjectLadderSnapshot.Hash(v1), 1));

        var v2 = ProjectLadderSnapshot.CanonicalizeV2(steps, edges);
        Assert.Contains("schema[2]", v2);
        Assert.Contains("Procedure", v2);
        Assert.NotEqual(ProjectLadderSnapshot.Hash(v1), ProjectLadderSnapshot.Hash(v2));
        Assert.True(ProjectLadderSnapshot.Verify(v2, ProjectLadderSnapshot.Hash(v2), 2));
    }

    [Fact]
    public void Typed_manifest_fails_closed_for_missing_kind_or_capability()
    {
        var definition = new VerificationArtifactDefinition(
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure),
            "HLRTP", "HLRTPCR", ReviewSubject.HighLevelSoftwareTest,
            ControlledDocumentType.HighLevelTestProcedures, RequirementLevel.HighLevel,
            VerificationArtifactCapability.Identity | VerificationArtifactCapability.Coverage);
        var registration = new VerificationArtifactConsumerRegistration("consumer", "typed consumer",
            [definition.Key], VerificationArtifactCapability.Identity);
        var missingKind = new VerificationArtifactConsumerRegistration("missing-kind", "typed consumer without key",
            [], VerificationArtifactCapability.Identity | VerificationArtifactCapability.Coverage);

        var manifest = LadderConsumerManifestCatalog.BuildForTestsV2([registration, missingKind], [definition]);

        Assert.False(manifest.IsReady);
        Assert.Contains(manifest.MissingArtifactCoverage,
            x => x.ArtifactKey == definition.Key && x.ConsumerId == "consumer" && !x.SupportsCapabilities);

        var missingKindManifest = LadderConsumerManifestCatalog.BuildV2(
            LadderConsumerManifestCatalog.RequiredConsumerIds
                .Select(id => (ILadderConsumerRegistration)new LadderConsumerRegistration(id, id)),
            [new VerificationArtifactConsumerRegistration("verification.coverage", "missing key", [],
                VerificationArtifactCapability.Identity | VerificationArtifactCapability.Coverage)],
            [definition]);
        Assert.False(missingKindManifest.IsReady);
        Assert.Contains(missingKindManifest.MissingArtifactCoverage,
            x => x.ConsumerId == "verification.coverage" && !x.SupportsKey);
    }

    [Fact]
    public void Typed_manifest_aggregates_relevant_capabilities_and_ignores_unrelated_consumers()
    {
        var definition = new VerificationArtifactDefinition(
            new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case),
            "HLRTP", "HLRTCR", ReviewSubject.HighLevelSoftwareTest,
            ControlledDocumentType.HighLevelTestProcedures, RequirementLevel.HighLevel,
            VerificationArtifactCapability.Identity | VerificationArtifactCapability.Coverage);
        var legacy = LadderConsumerManifestCatalog.RequiredConsumerIds
            .Select(id => (ILadderConsumerRegistration)new LadderConsumerRegistration(id, id));
        var typed = new IVerificationArtifactConsumerRegistration[]
        {
            new VerificationArtifactConsumerRegistration("verification.procedure-level", "identity", [definition.Key], VerificationArtifactCapability.Identity),
            new VerificationArtifactConsumerRegistration("verification.coverage", "coverage", [definition.Key], VerificationArtifactCapability.Coverage),
            new VerificationArtifactConsumerRegistration("navigation.primary", "unrelated", [], VerificationArtifactCapability.Execution),
        };

        var manifest = LadderConsumerManifestCatalog.BuildV2(legacy, typed, [definition]);

        Assert.True(manifest.IsReady);
        Assert.Empty(manifest.MissingArtifactCoverage);
    }

    [Theory]
    [InlineData(VerificationArtifactCapability.Coverage, "verification.coverage")]
    [InlineData(VerificationArtifactCapability.Execution, "release.readiness")]
    [InlineData(VerificationArtifactCapability.ControlledDocument, "baseline.controlled-documents")]
    public void Typed_manifest_fails_when_a_named_capability_lane_is_removed(
        VerificationArtifactCapability removedCapability, string consumerId)
    {
        var definition = VerificationArtifactVocabulary.Definition(
            new VerificationArtifactKey(VerificationDiscipline.System, VerificationArtifactKind.Procedure));
        var legacy = LadderConsumerManifestCatalog.RequiredConsumerIds
            .Select(id => (ILadderConsumerRegistration)new LadderConsumerRegistration(id, id));
        var typed = LadderConsumerManifestCatalog.RequiredConsumerIds
            .Select(id => LadderConsumerManifestCatalog.TypedRegistration(new LadderConsumerRegistration(id, id)))
            .Select(registration => registration.Id == consumerId
                ? registration with { SupportedCapabilities = registration.SupportedCapabilities & ~removedCapability }
                : registration)
            .ToArray();

        var manifest = LadderConsumerManifestCatalog.BuildV2(legacy, typed, [definition]);

        Assert.False(manifest.IsReady);
        Assert.Contains(manifest.MissingArtifactCoverage,
            x => x.ConsumerId == consumerId && !x.SupportsCapabilities);
    }
}
