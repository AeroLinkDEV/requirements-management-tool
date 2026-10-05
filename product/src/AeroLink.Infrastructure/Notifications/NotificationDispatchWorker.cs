using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AeroLink.Infrastructure.Notifications;

/// <summary>
/// Drains the notification outbox on a timer.
///
/// Nothing here is allowed to end the loop. A mail relay that is down, a database that is briefly
/// unreachable, a single malformed address — each must leave the worker running, because the queue is the
/// only thing standing between an approval and the person who has not noticed it. Failures are recorded on
/// the delivery rows, which are inspectable, rather than only in a log nobody reads.
/// </summary>
public sealed class NotificationDispatchWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<NotificationDispatchWorker> logger) : BackgroundService
{
    private TimeSpan Interval =>
        int.TryParse(configuration["Notifications:DispatchIntervalSeconds"], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(30);

    private int BatchSize =>
        int.TryParse(configuration["Notifications:BatchSize"], out var size) && size > 0 ? size : 50;

    private int MaximumAttempts =>
        int.TryParse(configuration["Notifications:MaximumAttempts"], out var attempts) && attempts > 0 ? attempts : 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
                var result = await dispatcher.DispatchAsync(BatchSize, stoppingToken);
                if (result.Sent + result.Failed + result.Suppressed > 0)
                    logger.LogInformation("Notification dispatch accepted {Accepted}, suppressed {Suppressed}, failed {Failed}.", result.Sent, result.Suppressed, result.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Notification dispatch blocked; inspect protected queue health. Category {Category}.", ex is Microsoft.EntityFrameworkCore.DbUpdateException ? "Persistence" : "ConfigurationOrPersistence");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
