using DotnetFleet.Core.Domain;

namespace DotnetFleet.Core.Interfaces;

public interface IFleetStorage
{
    // Projects
    Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default);
    Task<Project?> GetProjectAsync(Guid id, CancellationToken ct = default);
    Task AddProjectAsync(Project project, CancellationToken ct = default);
    Task UpdateProjectAsync(Project project, CancellationToken ct = default);
    Task DeleteProjectAsync(Guid id, CancellationToken ct = default);

    // Jobs
    Task<IReadOnlyList<DeploymentJob>> GetJobsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DeploymentJob>> GetJobsByProjectAsync(Guid projectId, CancellationToken ct = default);
    Task<DeploymentJob?> GetJobAsync(Guid id, CancellationToken ct = default);
    Task AddJobAsync(DeploymentJob job, CancellationToken ct = default);
    Task UpdateJobAsync(DeploymentJob job, CancellationToken ct = default);

    /// <summary>Returns the oldest Queued job that has not been assigned yet, or null.</summary>
    Task<DeploymentJob?> DequeueNextJobAsync(CancellationToken ct = default);

    // Logs
    Task<IReadOnlyList<LogEntry>> GetLogsAsync(Guid jobId, CancellationToken ct = default);
    Task AddLogEntriesAsync(IEnumerable<LogEntry> entries, CancellationToken ct = default);

    // Workers
    Task<IReadOnlyList<Worker>> GetWorkersAsync(CancellationToken ct = default);
    Task<Worker?> GetWorkerAsync(Guid id, CancellationToken ct = default);
    Task AddWorkerAsync(Worker worker, CancellationToken ct = default);
    Task UpdateWorkerAsync(Worker worker, CancellationToken ct = default);

    // Repo caches
    Task<IReadOnlyList<RepoCache>> GetRepoCachesAsync(Guid workerId, CancellationToken ct = default);
    Task<RepoCache?> GetRepoCacheAsync(Guid workerId, Guid projectId, CancellationToken ct = default);
    Task UpsertRepoCacheAsync(RepoCache cache, CancellationToken ct = default);
    Task DeleteRepoCacheAsync(Guid id, CancellationToken ct = default);

    // Users
    Task<User?> GetUserByUsernameAsync(string username, CancellationToken ct = default);
    Task<User?> GetUserAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken ct = default);
    Task AddUserAsync(User user, CancellationToken ct = default);
    Task UpdateUserAsync(User user, CancellationToken ct = default);
    Task DeleteUserAsync(Guid id, CancellationToken ct = default);
    Task<bool> AnyUserExistsAsync(CancellationToken ct = default);

    // Secrets
    /// <summary>Returns global secrets when <paramref name="projectId"/> is null, or project-scoped secrets otherwise.</summary>
    Task<IReadOnlyList<Secret>> GetSecretsAsync(Guid? projectId, CancellationToken ct = default);
    Task<Secret?> GetSecretAsync(Guid id, CancellationToken ct = default);
    Task AddSecretAsync(Secret secret, CancellationToken ct = default);
    Task UpdateSecretAsync(Secret secret, CancellationToken ct = default);
    Task DeleteSecretAsync(Guid id, CancellationToken ct = default);
}
