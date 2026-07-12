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
        services.AddScoped<ReleaseReadinessService>();
        services.AddScoped<ControlledOutputGenerator>();
        services.AddScoped<ChangeRequestOutputGenerator>();
        services.AddScoped<ReleaseExecutionService>();
        services.AddScoped<IdentityService>();
        services.AddScoped<IdentitySeeder>();
        services.AddScoped<EnterpriseRequirementsService>();
        services.AddScoped<EnterpriseWorkspaceSeeder>();
        return services;
    }
}
