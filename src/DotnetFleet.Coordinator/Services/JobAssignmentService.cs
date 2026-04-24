using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// Push-based smart scheduler. On every wake-up (event or 5s safety tick) it walks the
/// list of unassigned <c>Queued</c> jobs and pushes each one to the worker that yields
/// the lowest expected time-to-finish:
///
/// <code>eta(worker) = sum(estimate of jobs already in this worker's queue)
///                  + estimate(job, worker)</code>
///
/// This deliberately ignores whether the worker is currently <c>Busy</c>: a faster
/// machine with one job in flight may still beat an idle slow machine, and the smart
/// thing is to queue work behind it.
///
/// Compatibility filtering is intentionally permissive (any registered worker is a
/// candidate). The hook <see cref="IsCompatible"/> is the future extension point for
/// arch / OS / secrets matching.
/// </summary>
public class JobAssignmentService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly JobAssignmentSignal signal;
    private readonly LogBroadcaster broadcaster;
    private readonly ILogger<JobAssignmentService> logger;

    /// <summary>
    /// Safety-net interval. The scheduler is normally event-driven; this guarantees it
    /// re-evaluates even if a Notify was missed (e.g. clock skew on AssignedAt timestamps,
    /// or a worker that came online without anyone calling Notify).
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    public JobAssignmentService(
        IServiceScopeFactory scopeFactory,
        JobAssignmentSignal signal,
        LogBroadcaster broadcaster,
        ILogger<JobAssignmentService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.signal = signal;
        this.broadcaster = broadcaster;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First pass on startup so any jobs queued while the coordinator was down get
        // picked up immediately.
        signal.Notify();

        while (!stoppingToken.IsCancellationRequested)
        {
            using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            tickCts.CancelAfter(TickInterval);
            try
            {
                await signal.Reader.ReadAsync(tickCts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Safety tick — fall through and run a pass anyway.
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await AssignPendingAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in job assignment service");
            }
        }
    }

    /// <summary>
    /// Single assignment pass. Visible internally so tests can drive it directly without
    /// dealing with the wake-up channel.
    /// </summary>
    internal async Task AssignPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFleetStorage>();
        var estimator = scope.ServiceProvider.GetRequiredService<IDurationEstimator>();

        var queued = await storage.GetUnassignedQueuedJobsAsync(ct);
        if (queued.Count == 0)
            return;

        var workers = await storage.GetWorkersAsync(ct);

        // Pre-compute load (already-queued estimate sums) for each candidate worker.
        // Mutated in-place as we assign jobs in this pass so the next job in the same
        // pass sees the updated queue depth.
        var loadByWorker = new Dictionary<Guid, long>();
        foreach (var w in workers)
        {
            var active = await storage.GetActiveJobsForWorkerAsync(w.Id, ct);
            loadByWorker[w.Id] = SumEstimates(active);
        }

        foreach (var job in queued)
        {
            var candidates = workers.Where(w => IsCompatible(job, w)).ToList();
            if (candidates.Count == 0)
            {
                // Will be picked up by the warn loop in the reaper.
                continue;
            }

            (Worker Worker, long Eta, long Estimate)? best = null;
            foreach (var w in candidates)
            {
                var estimate = await estimator.EstimateAsync(job, w, ct);
                var eta = loadByWorker[w.Id] + estimate;
                if (best is null || eta < best.Value.Eta
                    // Deterministic tiebreaker: smaller worker GUID wins so two workers
                    // with identical specs never ping-pong.
                    || (eta == best.Value.Eta && w.Id.CompareTo(best.Value.Worker.Id) < 0))
                {
                    best = (w, eta, estimate);
                }
            }

            if (best is null) continue;

            var (winner, winnerEta, estimateMs) = best.Value;
            var ok = await storage.AssignJobToWorkerAsync(job.Id, winner.Id, estimateMs, ct);
            if (!ok)
            {
                // Lost the race (another assigner pass or a legacy claim). Move on.
                continue;
            }

            loadByWorker[winner.Id] = winnerEta;

            var line = $"[scheduler] Assigned to {DescribeWorker(winner)} " +
                       $"(estimate {FormatMs(estimateMs)}, queue depth before {FormatMs(winnerEta - estimateMs)}).";
            await EmitJobLogAsync(scope.ServiceProvider, job.Id, line, ct);
            logger.LogInformation("Assigned job {JobId} to worker {WorkerId} (eta={EtaMs}ms)",
                job.Id, winner.Id, winnerEta);
        }
    }

    /// <summary>
    /// Returns the queued estimate for a worker including remaining time on a Running job.
    /// </summary>
    private static long SumEstimates(IReadOnlyList<DeploymentJob> active)
    {
        var now = DateTimeOffset.UtcNow;
        long total = 0;
        foreach (var j in active)
        {
            var est = j.EstimatedDurationMs ?? EwmaDurationEstimator.DefaultMs;
            if (j.Status == JobStatus.Running && j.StartedAt is { } started)
            {
                var elapsedMs = (long)Math.Max(0, (now - started).TotalMilliseconds);
                est = Math.Max(1_000, est - elapsedMs); // floor at 1s so finishing-soon jobs still count.
            }
            total += est;
        }
        return total;
    }

    /// <summary>Compatibility hook. Today: every registered worker is a candidate.</summary>
    internal static bool IsCompatible(DeploymentJob job, Worker worker) => true;

    /// <summary>
    /// Sum of remaining estimated time across a worker's Assigned + Running jobs. Used
    /// both internally to compare ETAs and externally by the GetNextJob endpoint to
    /// decide work-stealing.
    /// </summary>
    internal static long ComputeWorkerLoadMs(IReadOnlyList<DeploymentJob> active) => SumEstimates(active);

    private static string DescribeWorker(Worker w) =>
        string.IsNullOrWhiteSpace(w.Name) ? w.Id.ToString("N")[..8] : w.Name;

    private static string FormatMs(long ms) =>
        ms >= 1_000 ? $"{ms / 1000.0:0.#}s" : $"{ms}ms";

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
            logger.LogWarning(ex, "Failed to persist scheduler log for job {JobId}", jobId);
        }
    }
}
