using DotnetFleet.Core.Domain;

namespace DotnetFleet.Core.Interfaces;

/// <summary>
/// Abstraction that workers use to obtain their next assigned job.
/// In-process workers call the storage directly; remote workers call the coordinator API.
/// </summary>
public interface IWorkerJobSource
{
    Task<DeploymentJob?> GetNextJobAsync(Guid workerId, CancellationToken ct = default);
    Task ReportJobStartedAsync(Guid jobId, Guid workerId, CancellationToken ct = default);
    Task SendLogChunkAsync(Guid jobId, IEnumerable<string> lines, CancellationToken ct = default);
    Task ReportJobCompletedAsync(Guid jobId, bool success, string? errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Posts a high-level phase event for the job (start/end/info). Used by the
    /// worker to surface deployment progress (e.g. <c>worker.git.clone</c>,
    /// <c>nuget.deploy</c>) to the coordinator UI without forcing humans to
    /// read the raw log stream.
    /// </summary>
    Task PostJobPhaseAsync(Guid jobId, PhaseEvent ev, CancellationToken ct = default);

    /// <summary>
    /// Asks the coordinator what the worker should do next for this job: keep going,
    /// stop because of a user-requested cancellation, or abort because the coordinator
    /// no longer considers the worker the owner (terminal state, lost ownership, …).
    /// </summary>
    Task<JobAction> GetJobActionAsync(Guid jobId, CancellationToken ct = default);
}
