using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// Stateless helper that, given a freshly received batch of log lines for a job,
/// detects the first GitVersion-style version string and persists it onto the job
/// exactly once. Subsequent batches are no-ops because <see cref="DeploymentJob.Version"/>
/// is already set.
/// </summary>
public static class DeploymentVersionTracker
{
    /// <summary>
    /// Scans <paramref name="lines"/> and, if the job has no version yet and a version is
    /// found, mutates <paramref name="job"/>, persists it through <paramref name="storage"/>,
    /// and returns the detected version. Returns null otherwise.
    /// </summary>
    public static async Task<string?> TryUpdateVersionAsync(
        IFleetStorage storage,
        DeploymentJob job,
        IEnumerable<string> lines,
        CancellationToken ct = default)
    {
        if (job.Version is not null) return null;

        foreach (var line in lines)
        {
            var version = GitVersionLineParser.TryExtract(line);
            if (version is null) continue;

            job.Version = version;
            await storage.UpdateJobAsync(job, ct);
            return version;
        }

        return null;
    }
}
