using DotnetFleet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// Periodically detects workers that stopped heartbeating and marks their
/// in-flight jobs as failed so they don't stay "Running" forever.
/// </summary>
public class StaleJobReaperService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly LogBroadcaster broadcaster;
    private readonly ILogger<StaleJobReaperService> logger;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan QueuedTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AssignedTimeout = TimeSpan.FromMinutes(5);

    public StaleJobReaperService(
        IServiceScopeFactory scopeFactory,
        LogBroadcaster broadcaster,
        ILogger<StaleJobReaperService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.broadcaster = broadcaster;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReapAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in stale job reaper");
            }

            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    private async Task ReapAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFleetStorage>();

        var offlineCount = await storage.MarkOfflineWorkersAsync(StaleThreshold, ct);
        if (offlineCount > 0)
            logger.LogWarning("Marked {Count} worker(s) as offline (no heartbeat for {Threshold}s)", offlineCount, StaleThreshold.TotalSeconds);

        var failedJobIds = await storage.FailStaleJobsAsync(StaleThreshold, ct);
        if (failedJobIds.Count > 0)
        {
            logger.LogWarning("Reaped {Count} stale job(s): {Ids}", failedJobIds.Count, string.Join(", ", failedJobIds));

            foreach (var jobId in failedJobIds)
                broadcaster.Complete(jobId);
        }

        var timedOutIds = await storage.FailTimedOutJobsAsync(QueuedTimeout, ct);
        if (timedOutIds.Count > 0)
        {
            logger.LogWarning("Timed out {Count} unclaimed job(s): {Ids}", timedOutIds.Count, string.Join(", ", timedOutIds));

            foreach (var jobId in timedOutIds)
                broadcaster.Complete(jobId);
        }

        var stuckAssignedIds = await storage.FailStuckAssignedJobsAsync(AssignedTimeout, ct);
        if (stuckAssignedIds.Count > 0)
        {
            logger.LogWarning("Failed {Count} stuck Assigned job(s): {Ids}", stuckAssignedIds.Count, string.Join(", ", stuckAssignedIds));

            foreach (var jobId in stuckAssignedIds)
                broadcaster.Complete(jobId);
        }
    }
}
