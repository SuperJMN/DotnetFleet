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
}
