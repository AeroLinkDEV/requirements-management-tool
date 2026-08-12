using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AeroLink.Infrastructure.Persistence;

public sealed class ManagedDocumentIntegrityWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ManagedDocumentIntegrityWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Evidence:IntegrityScanInitialDelayMinutes", 5), 1, 1440));
        var interval = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Evidence:IntegrityScanIntervalHours", 6), 1, 168));
        await Task.Delay(initialDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
                var projectIds = await db.ManagedDocuments.AsNoTracking().Select(x => x.ProjectId).Distinct().ToListAsync(stoppingToken);
                var integrity = scope.ServiceProvider.GetRequiredService<ManagedDocumentIntegrityService>();
                foreach (var projectId in projectIds)
                    await integrity.ScanProjectAsync(projectId, "system.integrity", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "The periodic managed-document integrity scan failed."); }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
