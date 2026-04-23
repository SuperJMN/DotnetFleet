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

    /// <summary>
    /// Atomically claims the oldest Queued job for the given worker.
    /// Transitions the job to <see cref="JobStatus.Assigned"/> and sets <c>WorkerId</c> in a single
    /// serialized transaction so concurrent workers cannot pick the same job.
    /// Returns the claimed job, or null if none was available.
    /// </summary>
    Task<DeploymentJob?> ClaimNextJobAsync(Guid workerId, CancellationToken ct = default);

    // Logs
    Task<IReadOnlyList<LogEntry>> GetLogsAsync(Guid jobId, CancellationToken ct = default);
    Task AddLogEntriesAsync(IEnumerable<LogEntry> entries, CancellationToken ct = default);

    // Workers
    Task<IReadOnlyList<Worker>> GetWorkersAsync(CancellationToken ct = default);
    Task<Worker?> GetWorkerAsync(Guid id, CancellationToken ct = default);
    Task AddWorkerAsync(Worker worker, CancellationToken ct = default);
    Task UpdateWorkerAsync(Worker worker, CancellationToken ct = default);

    /// <summary>
    /// Cheap liveness ping: updates <c>LastSeenAt = UtcNow</c> for the worker
    /// and, if it was <see cref="WorkerStatus.Offline"/>, flips it back to
    /// <see cref="WorkerStatus.Online"/>. Returns <c>true</c> if the worker exists.
    /// Designed to be called from any authenticated worker endpoint so that
    /// heartbeats are not the single point of failure for liveness detection.
    /// </summary>
    Task<bool> TouchWorkerAsync(Guid workerId, CancellationToken ct = default);

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

    // Stale detection
    /// <summary>
    /// Marks workers whose <c>LastSeenAt</c> is older than <paramref name="staleThreshold"/> as <see cref="WorkerStatus.Offline"/>.
    /// Returns the number of workers affected.
    /// </summary>
    Task<int> MarkOfflineWorkersAsync(TimeSpan staleThreshold, CancellationToken ct = default);

    /// <summary>
    /// Fails jobs that are still <see cref="JobStatus.Running"/> or <see cref="JobStatus.Assigned"/>
    /// but whose worker has not heartbeated within <paramref name="staleThreshold"/>.
    /// Returns the IDs of the jobs that were marked as failed.
    /// </summary>
    Task<IReadOnlyList<Guid>> FailStaleJobsAsync(TimeSpan staleThreshold, CancellationToken ct = default);

    /// <summary>
    /// Fails jobs that have been <see cref="JobStatus.Queued"/> longer than <paramref name="queuedTimeout"/>
    /// without being claimed by a worker.
    /// Returns the IDs of the jobs that were marked as failed.
    /// </summary>
    Task<IReadOnlyList<Guid>> FailTimedOutJobsAsync(TimeSpan queuedTimeout, CancellationToken ct = default);

    /// <summary>
    /// Fails jobs that have been <see cref="JobStatus.Assigned"/> longer than <paramref name="assignedTimeout"/>
    /// without transitioning to <see cref="JobStatus.Running"/>. This catches jobs that were claimed
    /// by a worker but never started (e.g. due to a crash between claim and execution).
    /// Returns the IDs of the jobs that were marked as failed.
    /// </summary>
    Task<IReadOnlyList<Guid>> FailStuckAssignedJobsAsync(TimeSpan assignedTimeout, CancellationToken ct = default);

    /// <summary>
    /// Fails every non-terminal job (<see cref="JobStatus.Assigned"/> or <see cref="JobStatus.Running"/>)
    /// owned by <paramref name="workerId"/>. Used when the worker explicitly announces that it is no longer
    /// running anything (e.g. it just (re)started and reported <see cref="WorkerStatus.Online"/>): if the
    /// coordinator still has live jobs assigned to it, the worker crashed mid-job.
    /// Returns the IDs of the jobs that were marked as failed.
    /// </summary>
    Task<IReadOnlyList<Guid>> FailJobsForWorkerAsync(Guid workerId, string reason, CancellationToken ct = default);

    // Secrets
    /// <summary>Returns global secrets when <paramref name="projectId"/> is null, or project-scoped secrets otherwise.</summary>
    Task<IReadOnlyList<Secret>> GetSecretsAsync(Guid? projectId, CancellationToken ct = default);
    Task<Secret?> GetSecretAsync(Guid id, CancellationToken ct = default);
    Task AddSecretAsync(Secret secret, CancellationToken ct = default);
    Task UpdateSecretAsync(Secret secret, CancellationToken ct = default);
    Task DeleteSecretAsync(Guid id, CancellationToken ct = default);
}
