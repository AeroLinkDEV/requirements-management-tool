using AeroLink.Infrastructure.Notifications;
using AeroLink.Domain.Contracts;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;

namespace AeroLink.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAeroLinkInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IProjectLadderPolicyResolver, EffectiveProjectLadderPolicyResolver>();
        services.AddScoped<ProjectLadderSealAuthority>();
        services.AddScoped<ProjectLadderUpgradeAuthority>();
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
            new LadderConsumerRegistration("navigation.primary", "Project-ladder-aware primary navigation and surfaces"),
        };
        foreach (var registration in legacyRegistrations)
            services.AddSingleton<ILadderConsumerRegistration>(registration);
        // These declarations live beside the routed infrastructure seams. Do not infer artifact obligations from
        // the legacy string inventory: a consumer that happens to have a familiar ID is not thereby a handler for
        // every kind or capability. Software Procedure packages route through the shared identity/review and
        // exact Case-to-Procedure coverage seams. Controlled documents cover every package key through the
        // same renderer/register seam; execution remains #726's gate.
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
                "Release readiness policy gates", currentArtifactKeys,
                VerificationArtifactCapability.Execution),
        };
        foreach (var registration in typedRegistrations)
            services.AddSingleton(registration);
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connection = configuration.GetConnectionString("AeroLink") ?? "Data Source=aerolink-dev.db";
        // An unrecognised provider used to fall through to SQLite, so an installer who wrote "Postgres"
        // instead of "PostgreSql" got a SQLite parser complaining that 'host' is not a supported keyword —
        // a message that names neither the mistake nor the setting that caused it. Say what was wrong.
        var isPostgres = provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase);
        if (!isPostgres && !provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Database:Provider is '{provider}'. AeroLink supports 'PostgreSql' and 'Sqlite'.");
        services.AddSingleton<ReleasedExecutionEvidenceInterceptor>();
        services.AddDbContext<AeroLinkDbContext>((serviceProvider, options) =>
        {
            if (isPostgres) options.UseNpgsql(connection);
            else options.UseSqlite(connection);
            options.AddInterceptors(serviceProvider.GetRequiredService<ReleasedExecutionEvidenceInterceptor>());
        });
        services.AddScoped<IChangeRequestRepository, ChangeRequestRepository>();
        services.AddScoped<IProgramRepository, ProgramRepository>();
        services.AddScoped<IBaselineRepository, BaselineRepository>();
        services.AddScoped<RequirementBaselineMaterializer>();
        services.AddScoped<ExactLinkLifecycleService>();
        services.AddScoped<TestProcedureBaselineMaterializer>();
        services.AddScoped<VerificationProcedureAuthoringService>();
        services.AddScoped<LegacyProcedureManifestBootstrapper>();
        services.AddScoped<FmsShowcaseSeeder>();
        services.AddScoped<SecondShowcaseSeeder>();
        services.AddScoped<ImportPracticeSeeder>();
        services.AddScoped<NotificationOutbox>();
        services.AddScoped<NotificationLinkBuilder>();
        services.AddSingleton<UnsubscribeTokenService>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddHostedService<NotificationDispatchWorker>();
        services.AddSingleton<EvidenceFileStore>();
        services.AddScoped<ManagedDocumentFileService>();
        services.AddSingleton<IManagedDocumentStorageFaultInjector, NoManagedDocumentStorageFaultInjector>();
        services.AddScoped<ManagedDocumentStorageCoordinator>();
        services.AddScoped<ManagedDocumentIntegrityService>();
        services.AddScoped<ManagedDocumentShowcaseSeeder>();
        services.AddHostedService<ManagedDocumentIntegrityWorker>();
        services.AddHostedService<EnterpriseJobWorker>();
        services.AddDataProtection();
        services.AddHttpClient("AeroLinkWebhooks", client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHostedService<WebhookDeliveryWorker>();
        services.AddScoped<IntegrationSecurityService>();
        services.AddHttpClient("jira", client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IJiraClient, JiraClient>();
        services.AddScoped<JiraConnectorService>();
        services.AddHostedService<JiraStatusWorker>();
        services.AddScoped<IntegrationEventPublisher>();
        services.AddScoped<ReleaseReadinessService>();
        services.AddScoped<RichContentPublisher>();
        services.AddScoped<ControlledOutputGenerator>();
        services.AddScoped<SoftwareVerificationCaseMigrationAuthority>();
        services.AddScoped<TestChangeRequestPrefixMigrationAuthority>();
        services.AddScoped<DraftDocumentGenerator>();
        services.AddScoped<VariantConfigurationProjectionService>();
        services.AddScoped<VariantPublicationGenerator>();
        services.AddScoped<ChangeRequestOutputGenerator>();
        services.AddScoped<TestChangeRequestOutputGenerator>();
        services.AddScoped<ReleaseExecutionService>();
        services.AddScoped<VerificationImpactService>();
        services.AddScoped<ProblemReportLinkService>();
        services.AddScoped<DownstreamImpactService>();
        services.AddScoped<BuildTestSetService>();
        services.AddScoped<IdentityService>();
        services.AddScoped<ProjectLadderAuthoringService>();
        services.AddScoped<ProjectVerificationVocabularyService>();
        services.AddScoped<IdentitySeeder>();
        services.AddScoped<ExternalIdentityAdministrationService>();
        services.AddScoped<EnterpriseRequirementsService>();
        services.AddScoped<IControlledEditingAdapter, SystemChangeRequestControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, RequirementProposalControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, SpecificationStructureControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, TraceLinkProposalControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, ReleasePlanningControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, DocumentTemplateControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, ProblemReportControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, ConfigurationChangeSetControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, TestChangeRequestControlledEditingAdapter>();
        services.AddScoped<ControlledEditingCheckInEngine>();
        services.AddScoped<ReqIfExchangeService>();
        services.AddScoped<EnterpriseWorkspaceSeeder>();
        services.AddScoped<TestProcedureDocumentBootstrap>();
        return services;
    }
}
