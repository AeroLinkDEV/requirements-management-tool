using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Infrastructure;

// One declaration inventory for application composition and standalone fixture initialization.
internal static class InfrastructureLadderConsumers
{
    internal static void Register(IServiceCollection services)
    {
        // These are the complete stable ladder seams. Registration is intentionally explicit: the manifest is
        // a readiness inventory, not a flag, and each entry is backed by a project-effective policy route.
        var legacyRegistrations = new[]
        {
            new LadderConsumerRegistration("change-request.authoring", "Change-request level/type acceptance and authoring"),
            new LadderConsumerRegistration("change-request.identifier-allocation", "Requirement and change-request controlled prefixes"),
            new LadderConsumerRegistration("change-request.upstream-allocation", "Upstream picker and exact parent validation"),
            new LadderConsumerRegistration("change-request.downstream-impact", "Approved-change downstream assessment creation"),
            new LadderConsumerRegistration("reqif.commit", "ReqIF imported-level parsing and commit allocation"),
            new LadderConsumerRegistration("enterprise.import-aliases", "Enterprise level import aliases"),
            new LadderConsumerRegistration("trace.generic-mutation", "Generic trace mutation acceptance/refusal"),
            new LadderConsumerRegistration("controlled-editing.identity", "Controlled editing identity and check-in"),
            new LadderConsumerRegistration("approval.workflow-subject", "Approval workflow subject level and scope"),
            new LadderConsumerRegistration("verification.procedure-level", "Verification artifact level mapping"),
            new LadderConsumerRegistration("verification.test-change-workflow", "Test-change workflow disciplines and prefixes"),
            new LadderConsumerRegistration("verification.coverage", "Same-level coverage mutation and persistence validation"),
            new LadderConsumerRegistration("baseline.controlled-documents", "Baseline controlled-document derivation"),
            new LadderConsumerRegistration("build.test-sets", "Build verification test-set derivation"),
            new LadderConsumerRegistration("enterprise.schema-catalogue", "Enterprise schema/specification catalogue synchronization"),
            new LadderConsumerRegistration("release.readiness", "Release readiness policy gates"),
            new LadderConsumerRegistration("release.reconciliation", "Release trace reconciliation policy"),
            new LadderConsumerRegistration("verification.execution", "Execution creation and latest build-scoped result resolution"),
            new LadderConsumerRegistration("baseline.executable-materialization", "Baseline executable artifact selection and materialization"),
            new LadderConsumerRegistration("navigation.primary", "Project-ladder-aware primary navigation and surfaces"),
        };
        foreach (var registration in legacyRegistrations)
            services.AddSingleton<ILadderConsumerRegistration>(registration);
        // These declarations live beside the routed infrastructure seams. Do not infer artifact obligations from
        // the legacy string inventory: a consumer that happens to have a familiar ID is not thereby a handler for
        // every kind or capability. #726 reconciles the execution seams: every effective-execution consumer
        // declares the artifact keys and capabilities it actually handles, so the typed v2 manifest fails
        // closed when a Procedure-capable execution consumer is absent or lacks the Procedure key.
        var systemProcedure = new VerificationArtifactKey(
            VerificationDiscipline.System, VerificationArtifactKind.Procedure);
        var highLevelCase = new VerificationArtifactKey(
            VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case);
        var lowLevelCase = new VerificationArtifactKey(
            VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Case);
        var highLevelProcedure = new VerificationArtifactKey(
            VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Procedure);
        var lowLevelProcedure = new VerificationArtifactKey(
            VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Procedure);
        var currentArtifactKeys = new[] { systemProcedure, highLevelCase, lowLevelCase };
        var packageArtifactKeys = currentArtifactKeys.Concat(new[] { highLevelProcedure, lowLevelProcedure }).ToArray();
        var typedRegistrations = new IVerificationArtifactConsumerRegistration[]
        {
            new VerificationArtifactConsumerRegistration("change-request.downstream-impact",
                "Approved-change downstream assessment creation", packageArtifactKeys,
                VerificationArtifactCapability.ChangeReview),
            new VerificationArtifactConsumerRegistration("verification.procedure-level",
                "Verification artifact level mapping", packageArtifactKeys,
                VerificationArtifactCapability.Identity | VerificationArtifactCapability.Header
                | VerificationArtifactCapability.Revision | VerificationArtifactCapability.Lifecycle),
            new VerificationArtifactConsumerRegistration("verification.test-change-workflow",
                "Test-change workflow disciplines and prefixes", packageArtifactKeys,
                VerificationArtifactCapability.ChangeReview),
            new VerificationArtifactConsumerRegistration("verification.coverage",
                "Same-level coverage mutation and persistence validation", packageArtifactKeys,
                VerificationArtifactCapability.Coverage),
            new VerificationArtifactConsumerRegistration("baseline.controlled-documents",
                "Baseline controlled-document derivation", packageArtifactKeys,
                VerificationArtifactCapability.ControlledDocument),
            new VerificationArtifactConsumerRegistration("release.readiness",
                "Release readiness policy gates", packageArtifactKeys,
                VerificationArtifactCapability.Execution | VerificationArtifactCapability.Coverage),
            new VerificationArtifactConsumerRegistration("build.test-sets",
                "Build verification test-set derivation and planning", packageArtifactKeys,
                VerificationArtifactCapability.Execution),
            new VerificationArtifactConsumerRegistration("release.reconciliation",
                "Release trace reconciliation policy", packageArtifactKeys,
                VerificationArtifactCapability.Execution | VerificationArtifactCapability.Coverage),
            new VerificationArtifactConsumerRegistration("verification.execution",
                "Execution creation, import, and latest build-scoped result resolution", packageArtifactKeys,
                VerificationArtifactCapability.Execution),
            new VerificationArtifactConsumerRegistration("baseline.executable-materialization",
                "Baseline executable artifact selection and materialization", packageArtifactKeys,
                VerificationArtifactCapability.Execution),
            new VerificationArtifactConsumerRegistration("navigation.primary",
                "Project-ladder-aware navigation, search, and workspace projections of effective execution",
                packageArtifactKeys,
                VerificationArtifactCapability.Identity | VerificationArtifactCapability.Header
                | VerificationArtifactCapability.Revision | VerificationArtifactCapability.Lifecycle
                | VerificationArtifactCapability.Execution),
        };
        foreach (var registration in typedRegistrations)
            services.AddSingleton(registration);
    }
}
