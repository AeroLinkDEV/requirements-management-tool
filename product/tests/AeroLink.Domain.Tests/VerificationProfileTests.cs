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

    [Fact]
    public void Current_verification_artifacts_expose_one_neutral_header_and_typed_revision_content()
    {
        var now = DateTimeOffset.UtcNow;
        var system = new TestProcedure(Guid.NewGuid(), "SYSTP-000001", "System procedure", "tester", now,
            TestProcedureLevel.System);
        var software = new TestProcedure(Guid.NewGuid(), "HLRTP-000001", "Software case", "tester", now,
            TestProcedureLevel.HighLevel);
        var revision = new TestProcedureRevision(software.Id, 0, "Objective", "Logical preconditions",
            "Coverage", "Pass criteria", TestProcedureState.Approved, "tester", now);

        Assert.Equal(new VerificationArtifactKey(VerificationDiscipline.System, VerificationArtifactKind.Procedure), system.ArtifactKey);
        Assert.Equal(new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case), software.ArtifactKey);
        Assert.Equal(software.ArtifactKey, software.Header.ArtifactKey);
        Assert.Equal(VerificationArtifactKind.Case, revision.Content(software.ArtifactKey).Kind);
        Assert.Equal("Logical preconditions", ((VerificationCaseRevisionContent)revision.Content(software.ArtifactKey)).Preconditions);
        Assert.Equal(VerificationArtifactLifecycleState.Active, revision.RevisionHeader(software.ArtifactKey).State);
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

        var softwareProcedure = steps.Append(new LadderStepDraft("HighLevel", 2,
            LegacyLadderPolicy.Instance.Definition(RequirementLevel.HighLevel).Capabilities,
            [VerificationArtifactKind.Case, VerificationArtifactKind.Procedure])).ToArray();
        var softwareCase = softwareProcedure.Select(x => x.CatalogueEntry == "HighLevel"
            ? x with { EnabledArtifactKinds = [VerificationArtifactKind.Case] }
            : x).ToArray();
        Assert.NotEqual(ProjectLadderSnapshot.HashV2(softwareProcedure, edges),
            ProjectLadderSnapshot.HashV2(softwareCase, edges));
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
    public void Authored_no_verification_capability_never_receives_a_catalogue_default()
    {
        var noVerification = new LadderStepDraft(nameof(RequirementLevel.HighLevel), 1,
            LevelCapabilities.HasChangeControl, null);
        var (validated, _) = ProjectLadderDraftValidator.Validate([noVerification], [], LegacyLadderPolicy.Instance);
        Assert.Null(validated[0].EnabledArtifactKinds);
        var canonical = ProjectLadderSnapshot.CanonicalizeV2(validated, []);
        Assert.DoesNotContain("Case", canonical, StringComparison.Ordinal);
        var enabled = noVerification with { Capabilities = LevelCapabilities.HasChangeControl | LevelCapabilities.HasVerification };
        Assert.NotEqual(ProjectLadderSnapshot.HashV2([noVerification], []), ProjectLadderSnapshot.HashV2([enabled], []));
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
