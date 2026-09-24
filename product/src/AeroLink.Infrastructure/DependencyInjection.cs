using AeroLink.Domain.Assurance;
using AeroLink.Infrastructure.Diagnostics;
using AeroLink.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;
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
        services.AddScoped<IProjectAssurancePolicyResolver, EffectiveProjectAssurancePolicyResolver>();
        services.AddScoped<ProjectLadderSealAuthority>();
        services.AddScoped<ProjectLadderUpgradeAuthority>();
        services.AddScoped<SoftwareProcedureExecutionCutoverAuthority>();
        services.AddScoped<FrozenReviewTraceAdjacencyMigrationAuthority>();
        InfrastructureLadderConsumers.Register(services);
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
        // #939 stall diagnostics. Both are opt-in; the browser harness sets them and nothing else does.
        var slowDatabaseAfter = StallDiagnosticsSettings.SlowDatabaseAfter(configuration);
        if (slowDatabaseAfter is { } slowThreshold)
            services.AddSingleton(provider => new SlowDatabaseInterceptor(slowThreshold,
                provider.GetRequiredService<ILogger<SlowDatabaseInterceptor>>()));
        services.AddSingleton<InFlightRequests>();
        if (StallDiagnosticsSettings.StallReportAfter(configuration) is { } stallThreshold)
            services.AddHostedService(provider => new StallWatchdog(provider.GetRequiredService<InFlightRequests>(),
                stallThreshold, provider.GetRequiredService<ILogger<StallWatchdog>>()));
        services.AddDbContext<AeroLinkDbContext>((serviceProvider, options) =>
        {
            if (isPostgres) options.UseNpgsql(connection);
            else options.UseSqlite(connection);
            options.AddInterceptors(serviceProvider.GetRequiredService<ReleasedExecutionEvidenceInterceptor>());
            if (slowDatabaseAfter is not null)
                options.AddInterceptors(serviceProvider.GetRequiredService<SlowDatabaseInterceptor>());
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
        services.AddScoped<ControlledAttachmentStorageCoordinator>();
        services.AddScoped<ManagedDocumentIntegrityService>();
        services.AddScoped<ManagedDocumentShowcaseSeeder>();
        services.AddHostedService<ManagedDocumentIntegrityWorker>();
        services.AddHostedService<EnterpriseJobWorker>();
        services.AddDataProtection();
        services.AddSingleton<IWebhookDnsResolver, SystemWebhookDnsResolver>();
        services.AddSingleton<WebhookDestinationPolicy>();
        services.AddHttpClient("AeroLinkWebhooks", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => WebhookConnectionTransport.CreateHandler(TimeSpan.FromSeconds(15)));
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
        services.AddScoped<ProjectLeadershipService>();
        services.AddScoped<ProjectLeadershipMigrationAuthority>();
        services.AddScoped<ProjectLeadershipReconciliationAuthority>();
        services.AddScoped<TestChangeRequestPrefixMigrationAuthority>();
        services.AddScoped<DraftDocumentGenerator>();
        services.AddScoped<VariantConfigurationProjectionService>();
        services.AddScoped<VariantPublicationGenerator>();
        services.AddScoped<ChangeRequestOutputGenerator>();
        services.AddScoped<TestChangeRequestOutputGenerator>();
        services.AddScoped<ProblemReportOutputGenerator>();
        services.AddScoped<ReleaseExecutionService>();
        services.AddScoped<VerificationImpactService>();
        services.AddScoped<ProblemReportLinkService>();
        services.AddScoped<DownstreamImpactService>();
        services.AddScoped<BuildTestSetService>();
        services.AddScoped<ProjectAuthorityResolver>();
        services.AddScoped<IdentityService>();
        services.AddScoped<ProjectLadderAuthoringService>();
        services.AddScoped<ProjectSetupService>();
        services.AddScoped<ProjectSetupInceptionService>();
        services.Configure<ProjectGitLabOptions>(configuration.GetSection("ProjectGitLab"));
        services.AddHttpClient<GitLabProjectConnectionProbe>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 4 });
        // One limit for a metadata read: the client's and the reader's own whole-request timer agree.
        services.AddHttpClient<GitLabMetadataReader>((provider, client) => client.Timeout =
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProjectGitLabOptions>>().Value.MetadataRequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 4 });
        services.AddSingleton<GitLabDisplayMetadataCache>();
        services.AddScoped<SoftwareReleaseIdentityAuthority>();
        services.AddScoped<ReleasedSyntheticSourceSupplementService>();
        services.AddScoped<ProjectAssurancePolicyService>();
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
