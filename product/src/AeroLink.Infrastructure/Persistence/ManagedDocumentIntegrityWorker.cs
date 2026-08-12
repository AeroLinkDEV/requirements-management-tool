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
                var documentProjectIds = db.ManagedDocuments.AsNoTracking().Select(x => x.ProjectId);
                var interruptedProjectIds = db.ManagedDocumentStorageOperations.AsNoTracking()
                    .Where(x => x.State == AeroLink.Domain.Documents.ManagedDocumentStorageOperationState.Pending
                        || x.State == AeroLink.Domain.Documents.ManagedDocumentStorageOperationState.RepairRequired)
                    .Select(x => x.ProjectId);
                var projectIds = await documentProjectIds.Union(interruptedProjectIds).ToListAsync(stoppingToken);
                var reconciliation = scope.ServiceProvider.GetRequiredService<ManagedDocumentStorageCoordinator>();
                foreach (var projectId in projectIds)
                    await reconciliation.ReconcileProjectAsync(projectId, "system.integrity", DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "The periodic managed-document integrity scan failed."); }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
