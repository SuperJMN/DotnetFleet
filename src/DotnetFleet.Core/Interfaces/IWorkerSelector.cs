using DotnetFleet.Core.Domain;

namespace DotnetFleet.Core.Interfaces;

/// <summary>
/// Strategy that picks the best worker to receive a new deployment from a set of
/// idle candidates. Implementations must be deterministic and side-effect free so
/// that selection can be safely replayed during tests and concurrent claims.
/// </summary>
public interface IWorkerSelector
{
    /// <summary>
    /// Computes a numeric capability score for <paramref name="worker"/>. Higher is better.
    /// Workers with no reported capabilities (legacy) score 0 so they still get picked
    /// when they are the only candidate, but lose to any worker that reports specs.
    /// </summary>
    int Score(Worker worker);

    /// <summary>
    /// Returns the best candidate from <paramref name="candidates"/> (highest score,
    /// ties broken deterministically by <see cref="Worker.Name"/> then <see cref="Worker.Id"/>),
    /// or <c>null</c> if the sequence is empty.
    /// </summary>
    Worker? SelectBest(IEnumerable<Worker> candidates);
}
