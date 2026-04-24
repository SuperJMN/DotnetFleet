using DotnetFleet.Core.Domain;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// Predicts how long a deployment job will take on a specific worker. Used by the
/// <c>JobAssignmentService</c> to decide which worker yields the lowest expected
/// time-to-finish for a queued job.
/// </summary>
public interface IDurationEstimator
{
    /// <summary>
    /// Estimated wall-clock duration in milliseconds for running <paramref name="job"/>
    /// on <paramref name="worker"/>. Always returns a positive value (never null).
    /// </summary>
    Task<long> EstimateAsync(DeploymentJob job, Worker worker, CancellationToken ct = default);
}
