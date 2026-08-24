using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Infrastructure.Tests;

public sealed class LadderConsumerRegistrationTests
{
    [Fact]
    public void Infrastructure_registers_the_complete_stable_consumer_manifest()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Provider"] = "Sqlite" })
            .Build();

        services.AddAeroLinkInfrastructure(configuration);

        var registrations = services
            .Where(x => x.ServiceType == typeof(ILadderConsumerRegistration))
            .Select(x => Assert.IsType<LadderConsumerRegistration>(x.ImplementationInstance))
            .ToArray();
        var ids = registrations.Select(x => x.Id).ToArray();
        Assert.Equal(new[]
        {
            "change-request.authoring",
            "change-request.identifier-allocation",
            "change-request.upstream-allocation",
            "change-request.downstream-impact",
            "reqif.commit",
            "enterprise.import-aliases",
            "trace.generic-mutation",
            "controlled-editing.identity",
            "approval.workflow-subject",
            "verification.procedure-level",
            "verification.test-change-workflow",
            "verification.coverage",
            "baseline.controlled-documents",
            "build.test-sets",
            "enterprise.schema-catalogue",
            "release.readiness",
            "release.reconciliation",
            "navigation.primary",
        }, ids);

        var manifest = LadderConsumerManifestCatalog.BuildForRegistrations(registrations);
        Assert.True(manifest.IsReady);
        Assert.DoesNotContain(manifest.MissingOrUnrouted, x => ids.Contains(x.Id, StringComparer.Ordinal));
        Assert.Empty(manifest.MissingOrUnrouted);

        var typed = services
            .Where(x => x.ServiceType == typeof(IVerificationArtifactConsumerRegistration))
            .Select(x => Assert.IsAssignableFrom<IVerificationArtifactConsumerRegistration>(x.ImplementationInstance))
            .ToArray();
        Assert.Equal(new[]
        {
            "verification.procedure-level",
            "verification.test-change-workflow",
            "verification.coverage",
            "baseline.controlled-documents",
            "release.readiness",
        }, typed.Select(x => x.Id).ToArray());
        var packageConsumers = typed.Where(x => x.Id is
            "verification.procedure-level" or "verification.test-change-workflow").ToArray();
        Assert.Equal(2, packageConsumers.Length);
        Assert.All(packageConsumers, registration =>
            Assert.Contains(registration.SupportedArtifactKeys,
                x => x == new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure)));
        Assert.All(packageConsumers, registration =>
            Assert.Contains(registration.SupportedArtifactKeys,
                x => x == new VerificationArtifactKey(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Procedure)));
        Assert.DoesNotContain(typed.Where(x => !packageConsumers.Contains(x)), registration =>
            registration.SupportedArtifactKeys.Any(x => x.Kind == VerificationArtifactKind.Procedure
                && x.Discipline != VerificationDiscipline.System));
        Assert.Equal(VerificationArtifactCapability.Coverage,
            typed.Single(x => x.Id == "verification.coverage").SupportedCapabilities);
        Assert.DoesNotContain(typed, x => x.Id == "navigation.primary");
        var currentProfile = new[]
        {
            VerificationArtifactVocabulary.Definition(new(VerificationDiscipline.System, VerificationArtifactKind.Procedure)),
            VerificationArtifactVocabulary.Definition(new(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case)),
            VerificationArtifactVocabulary.Definition(new(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Case)),
        };
        var typedManifest = LadderConsumerManifestCatalog.BuildV2(
            registrations.Cast<ILadderConsumerRegistration>().Concat(typed), typed,
            currentProfile);
        Assert.True(typedManifest.IsReady);

        var packageProfile = new[]
        {
            VerificationArtifactVocabulary.Definition(new(VerificationDiscipline.System, VerificationArtifactKind.Procedure)),
            VerificationArtifactVocabulary.Definition(new(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case)),
            VerificationArtifactVocabulary.Definition(new(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure)),
            VerificationArtifactVocabulary.Definition(new(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Case)),
            VerificationArtifactVocabulary.Definition(new(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Procedure)),
        };
        var packageManifest = LadderConsumerManifestCatalog.BuildV2(
            registrations.Cast<ILadderConsumerRegistration>().Concat(typed), typed, packageProfile);
        Assert.False(packageManifest.IsReady);
        Assert.Contains(packageManifest.MissingArtifactCoverage, x =>
            x.ArtifactKey == new VerificationArtifactKey(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure)
            && x.RequiredCapabilities.HasFlag(VerificationArtifactCapability.Execution));
        Assert.Contains(packageManifest.MissingArtifactCoverage, x =>
            x.ArtifactKey == new VerificationArtifactKey(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Procedure)
            && x.RequiredCapabilities.HasFlag(VerificationArtifactCapability.ControlledDocument));

        var untypedManifest = LadderConsumerManifestCatalog.BuildV2(registrations, [], currentProfile);
        Assert.False(untypedManifest.IsReady);
        Assert.NotEmpty(untypedManifest.MissingArtifactCoverage);
    }
}
