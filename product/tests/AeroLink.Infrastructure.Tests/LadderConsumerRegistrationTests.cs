using AeroLink.Domain.Hierarchy;
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
    }
}
