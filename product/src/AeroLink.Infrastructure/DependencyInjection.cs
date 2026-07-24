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
        services.AddDbContext<AeroLinkDbContext>(options =>
        {
            if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase)) options.UseNpgsql(connection);
            else options.UseSqlite(connection);
        });
        services.AddScoped<IScrRepository, ScrRepository>();
        services.AddScoped<IProgramRepository, ProgramRepository>();
        services.AddScoped<IBaselineRepository, BaselineRepository>();
        services.AddScoped<RequirementBaselineMaterializer>();
        services.AddScoped<FmsShowcaseSeeder>();
        services.AddSingleton<EvidenceFileStore>();
        services.AddHostedService<EnterpriseJobWorker>();
        services.AddDataProtection();
        services.AddHttpClient("AeroLinkWebhooks", client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHostedService<WebhookDeliveryWorker>();
        services.AddScoped<IntegrationSecurityService>();
        services.AddScoped<IntegrationEventPublisher>();
        services.AddScoped<ReleaseReadinessService>();
        services.AddScoped<ControlledOutputGenerator>();
        services.AddScoped<VariantConfigurationProjectionService>();
        services.AddScoped<VariantPublicationGenerator>();
        services.AddScoped<ChangeRequestOutputGenerator>();
        services.AddScoped<ReleaseExecutionService>();
        services.AddScoped<IdentityService>();
        services.AddScoped<IdentitySeeder>();
        services.AddScoped<ExternalIdentityAdministrationService>();
        services.AddScoped<FederatedIdentityRuntimeService>();
        services.AddScoped<EnterpriseRequirementsService>();
        services.AddScoped<IControlledEditingAdapter, SystemChangeRequestControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, RequirementProposalControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, SpecificationStructureControlledEditingAdapter>();
        services.AddScoped<IControlledEditingAdapter, TestProcedureControlledEditingAdapter>();
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
