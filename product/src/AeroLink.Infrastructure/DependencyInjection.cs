using AeroLink.Infrastructure.Notifications;
using AeroLink.Domain.Contracts;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAeroLinkInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
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
        services.AddScoped<TestProcedureBaselineMaterializer>();
        services.AddScoped<FmsShowcaseSeeder>();
        services.AddScoped<ImportPracticeSeeder>();
        services.AddScoped<NotificationOutbox>();
        services.AddScoped<NotificationLinkBuilder>();
        services.AddSingleton<UnsubscribeTokenService>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddHostedService<NotificationDispatchWorker>();
        services.AddSingleton<EvidenceFileStore>();
        services.AddScoped<ManagedDocumentFileService>();
        services.AddScoped<ManagedDocumentShowcaseSeeder>();
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
        services.AddScoped<DraftDocumentGenerator>();
        services.AddScoped<VariantConfigurationProjectionService>();
        services.AddScoped<VariantPublicationGenerator>();
        services.AddScoped<ChangeRequestOutputGenerator>();
        services.AddScoped<ReleaseExecutionService>();
        services.AddScoped<VerificationImpactService>();
        services.AddScoped<ProblemReportLinkService>();
        services.AddScoped<DownstreamImpactService>();
        services.AddScoped<BuildTestSetService>();
        services.AddScoped<IdentityService>();
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
        services.AddScoped<ControlledEditingCheckInEngine>();
        services.AddScoped<ReqIfExchangeService>();
        services.AddScoped<EnterpriseWorkspaceSeeder>();
        return services;
    }
}
