using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// Periodic janitor for fleet state. Responsibilities:
/// <list type="bullet">
///   <item>Mark workers as Offline when their heartbeat expires.</item>
///   <item>Fail Running jobs whose worker dropped off (we cannot recover an in-flight run).</item>
///   <item>Reclaim Assigned (queued, not yet started) jobs from offline workers and put them
///         back into the unassigned pool so the JobAssignmentService can re-route them.</item>
///   <item>Emit a warning log on jobs that have sat in Queued for too long with no compatible
///         workers — visible in the UI so the user knows why nothing is happening.</item>
/// </list>
/// Critically, it no longer fails Queued jobs solely for being old. As long as at least one
/// compatible worker exists (online or offline) the job waits.
/// </summary>
public class StaleJobReaperService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly LogBroadcaster broadcaster;
    private readonly ILogger<StaleJobReaperService> logger;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan OrphanWarnAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OrphanWarnEvery = TimeSpan.FromMinutes(5);

    /// <summary>Last time we emitted an orphan warning per job, to avoid log spam.</summary>
    private readonly Dictionary<Guid, DateTimeOffset> lastOrphanWarn = new();

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
        var signal = scope.ServiceProvider.GetRequiredService<JobAssignmentSignal>();

        var offlineCount = await storage.MarkOfflineWorkersAsync(StaleThreshold, ct);
        if (offlineCount > 0)
        {
            logger.LogWarning("Marked {Count} worker(s) as offline (no heartbeat for {Threshold}s)", offlineCount, StaleThreshold.TotalSeconds);
            // A worker just went offline; the assigner needs to reconsider routing.
            signal.Notify();
        }

        var failedJobIds = await storage.FailStaleJobsAsync(StaleThreshold, ct);
        if (failedJobIds.Count > 0)
        {
            logger.LogWarning("Reaped {Count} stale running job(s): {Ids}", failedJobIds.Count, string.Join(", ", failedJobIds));
            foreach (var jobId in failedJobIds)
                broadcaster.Complete(jobId);
        }

        // Reclaim queued-on-offline-worker jobs back to the unassigned pool. They are NOT
        // failed — the smart scheduler will re-route them to whichever worker is healthy.
        var reclaimed = await storage.UnassignJobsOfOfflineWorkersAsync(StaleThreshold, ct);
        if (reclaimed.Count > 0)
        {
            logger.LogInformation("Reclaimed {Count} job(s) from offline workers: {Ids}", reclaimed.Count, string.Join(", ", reclaimed));
            foreach (var jobId in reclaimed)
            {
                await EmitJobLogAsync(scope.ServiceProvider, jobId,
                    "[scheduler] Worker went offline before the job started — returning to the queue.", ct);
            }
            signal.Notify();
        }

        await WarnOrphanedQueuedJobsAsync(scope.ServiceProvider, storage, ct);
    }

    private async Task WarnOrphanedQueuedJobsAsync(IServiceProvider provider, IFleetStorage storage, CancellationToken ct)
    {
        var queued = await storage.GetUnassignedQueuedJobsAsync(ct);
        if (queued.Count == 0)
        {
            lastOrphanWarn.Clear();
            return;
        }

        var workers = await storage.GetWorkersAsync(ct);
        var anyOnline = workers.Any(w => w.Status == WorkerStatus.Online);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in queued)
        {
            var age = now - job.EnqueuedAt;
            if (age < OrphanWarnAfter) continue;

            if (lastOrphanWarn.TryGetValue(job.Id, out var last) && now - last < OrphanWarnEvery)
                continue;

            string line;
            if (workers.Count == 0)
                line = $"[scheduler] No workers registered. Job has been waiting {(int)age.TotalMinutes}m.";
            else if (!anyOnline)
                line = $"[scheduler] All workers are offline. Job has been waiting {(int)age.TotalMinutes}m for one to come back.";
            else
                // Compatible workers exist and at least one is online, but the job still hasn't been
                // assigned. Most likely the assigner hasn't picked it up yet (race window) — log a soft
                // notice so the user has something to look at while we figure it out.
                line = $"[scheduler] Job has been queued for {(int)age.TotalMinutes}m without being assigned.";

            await EmitJobLogAsync(provider, job.Id, line, ct);
            lastOrphanWarn[job.Id] = now;
        }
    }

    private async Task EmitJobLogAsync(IServiceProvider provider, Guid jobId, string line, CancellationToken ct)
    {
        var storage = provider.GetRequiredService<IFleetStorage>();
        var entry = new LogEntry { JobId = jobId, Line = line, Timestamp = DateTimeOffset.UtcNow };
        try
        {
            await storage.AddLogEntriesAsync([entry], ct);
            broadcaster.Publish(jobId, entry);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write reaper log for job {JobId}", jobId);
        }
    }
}
