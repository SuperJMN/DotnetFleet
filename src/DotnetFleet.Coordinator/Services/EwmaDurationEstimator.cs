using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// EWMA-backed duration estimator with a deterministic fallback chain:
/// <list type="number">
///   <item>EWMA for <c>(project, worker)</c> if any sample exists.</item>
///   <item>Mean EWMA for the project across all workers, scaled by the ratio of capability
///         scores between the sampled workers and the target worker (a faster worker that
///         has never run the project gets a proportionally smaller estimate).</item>
///   <item><see cref="DefaultMs"/> when nothing else is known (cold start).</item>
/// </list>
/// </summary>
public class EwmaDurationEstimator(IFleetStorage storage, IWorkerSelector selector) : IDurationEstimator
{
    public const long DefaultMs = 30_000;

    public async Task<long> EstimateAsync(DeploymentJob job, Worker worker, CancellationToken ct = default)
    {
        var direct = await storage.GetJobDurationStatAsync(job.ProjectId, worker.Id, ct);
        if (direct is { Samples: > 0 })
            return Math.Max(1, (long)direct.EwmaMs);

        var projectStats = await storage.GetJobDurationStatsForProjectAsync(job.ProjectId, ct);
        if (projectStats.Count == 0)
            return DefaultMs;

        // Project has been run on other workers but not on this one. Take the mean and
        // adjust by the inverse ratio of capability scores: a 10× faster worker gets a 10×
        // smaller expected duration. Clamp scores to a positive minimum so a brand-new
        // worker (zero score) doesn't divide by zero.
        var workers = await storage.GetWorkersAsync(ct);
        var byId = workers.ToDictionary(w => w.Id);
        var targetScore = Math.Max(1, selector.Score(worker));

        var weighted = projectStats
            .Select(s =>
            {
                var sampleWorker = byId.TryGetValue(s.WorkerId, out var w) ? w : null;
                var sampleScore = sampleWorker is null ? targetScore : Math.Max(1, selector.Score(sampleWorker));
                return s.EwmaMs * sampleScore / targetScore;
            })
            .ToList();

        return Math.Max(1, (long)weighted.Average());
    }
}
