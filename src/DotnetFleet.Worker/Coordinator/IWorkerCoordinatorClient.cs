using DotnetFleet.Core.Domain;

namespace DotnetFleet.WorkerService.Coordinator;

/// <summary>
/// Thin HTTP client the worker uses for everything that is NOT job-flow related.
/// Job claim, start, log push and complete go through <see cref="DotnetFleet.Core.Interfaces.IWorkerJobSource"/>.
/// </summary>
public interface IWorkerCoordinatorClient
{
    Task<Worker?> GetSelfAsync(CancellationToken ct = default);
    Task SendHeartbeatAsync(Guid workerId, CancellationToken ct = default);
    Task SendHeartbeatAsync(Guid workerId, string? version, CancellationToken ct = default);
    Task UpdateStatusAsync(Guid workerId, WorkerStatus status, CancellationToken ct = default);

    Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct = default);
    Task<IReadOnlyList<Secret>> GetGlobalSecretsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Secret>> GetProjectSecretsAsync(Guid projectId, CancellationToken ct = default);

    Task<IReadOnlyList<RepoCache>> GetRepoCachesAsync(Guid workerId, CancellationToken ct = default);
    Task UpsertRepoCacheAsync(Guid workerId, RepoCache cache, CancellationToken ct = default);
    Task DeleteRepoCacheAsync(Guid workerId, Guid cacheId, CancellationToken ct = default);
}
