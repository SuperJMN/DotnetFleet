using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Coordinator.Data;

/// <summary>
/// EF Core implementation of IFleetStorage. Uses IDbContextFactory so each
/// operation creates its own short-lived DbContext — safe for concurrent
/// singletons (BackgroundServices) and scoped API handlers alike.
/// </summary>
public class EfFleetStorage(IDbContextFactory<FleetDbContext> factory) : IFleetStorage
{
    // Projects
    public async Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Projects.OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<Project?> GetProjectAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Projects.FindAsync([id], ct);
    }

    public async Task AddProjectAsync(Project project, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateProjectAsync(Project project, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Projects.Update(project);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteProjectAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var p = await db.Projects.FindAsync([id], ct);
        if (p != null)
        {
            db.Projects.Remove(p);
            await db.SaveChangesAsync(ct);
        }
    }

    // Jobs
    public async Task<IReadOnlyList<DeploymentJob>> GetJobsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentJobs.OrderByDescending(j => j.EnqueuedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeploymentJob>> GetJobsByProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentJobs
            .Where(j => j.ProjectId == projectId)
            .OrderByDescending(j => j.EnqueuedAt)
            .ToListAsync(ct);
    }

    public async Task<DeploymentJob?> GetJobAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentJobs.FindAsync([id], ct);
    }

    public async Task AddJobAsync(DeploymentJob job, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.DeploymentJobs.Add(job);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateJobAsync(DeploymentJob job, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.DeploymentJobs.Update(job);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DeploymentJob?> ClaimNextJobAsync(Guid workerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Serializable on Microsoft.Data.Sqlite maps to BEGIN IMMEDIATE, which acquires the
        // write lock up-front. This serializes concurrent claims so the SELECT+UPDATE pair
        // behaves atomically across workers.
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);

        var job = await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Queued)
            .OrderBy(j => j.EnqueuedAt)
            .FirstOrDefaultAsync(ct);

        if (job is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        job.Status = JobStatus.Assigned;
        job.WorkerId = workerId;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return job;
    }

    // Logs
    public async Task<IReadOnlyList<LogEntry>> GetLogsAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.LogEntries
            .Where(l => l.JobId == jobId)
            .OrderBy(l => l.Timestamp)
            .ToListAsync(ct);
    }

    public async Task AddLogEntriesAsync(IEnumerable<LogEntry> entries, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.LogEntries.AddRange(entries);
        await db.SaveChangesAsync(ct);
    }

    // Workers
    public async Task<IReadOnlyList<Worker>> GetWorkersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Workers.OrderBy(w => w.Name).ToListAsync(ct);
    }

    public async Task<Worker?> GetWorkerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Workers.FindAsync([id], ct);
    }

    public async Task AddWorkerAsync(Worker worker, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Workers.Add(worker);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateWorkerAsync(Worker worker, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Workers.Update(worker);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> TouchWorkerAsync(Guid workerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // ExecuteUpdate avoids the read+write round-trip and is safe to call
        // on the hot path of every authenticated worker request.
        var rows = await db.Workers
            .Where(w => w.Id == workerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.LastSeenAt, now)
                .SetProperty(w => w.Status,
                    w => w.Status == WorkerStatus.Offline ? WorkerStatus.Online : w.Status),
                ct);

        return rows > 0;
    }

    // Stale detection
    public async Task<int> MarkOfflineWorkersAsync(TimeSpan staleThreshold, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - staleThreshold;

        var staleWorkers = await db.Workers
            .Where(w => w.Status != WorkerStatus.Offline && w.LastSeenAt != null && w.LastSeenAt < cutoff)
            .ToListAsync(ct);

        foreach (var worker in staleWorkers)
            worker.Status = WorkerStatus.Offline;

        if (staleWorkers.Count > 0)
            await db.SaveChangesAsync(ct);

        return staleWorkers.Count;
    }

    public async Task<IReadOnlyList<Guid>> FailStaleJobsAsync(TimeSpan staleThreshold, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - staleThreshold;

        var staleJobs = await db.DeploymentJobs
            .Where(j => (j.Status == JobStatus.Running || j.Status == JobStatus.Assigned)
                        && j.WorkerId != null)
            .Join(db.Workers.Where(w => w.LastSeenAt != null && w.LastSeenAt < cutoff),
                  j => j.WorkerId, w => w.Id, (j, _) => j)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in staleJobs)
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = now;
            job.ErrorMessage = "Worker unresponsive — marked as failed by stale job reaper.";
        }

        if (staleJobs.Count > 0)
            await db.SaveChangesAsync(ct);

        return staleJobs.Select(j => j.Id).ToList();
    }

    public async Task<IReadOnlyList<Guid>> FailTimedOutJobsAsync(TimeSpan queuedTimeout, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - queuedTimeout;

        var timedOutJobs = await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Queued && j.EnqueuedAt < cutoff)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in timedOutJobs)
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = now;
            job.ErrorMessage = "Timed out — no worker claimed this job within the allowed window.";
        }

        if (timedOutJobs.Count > 0)
            await db.SaveChangesAsync(ct);

        return timedOutJobs.Select(j => j.Id).ToList();
    }

    public async Task<IReadOnlyList<Guid>> FailJobsForWorkerAsync(Guid workerId, string reason, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var liveJobs = await db.DeploymentJobs
            .Where(j => j.WorkerId == workerId
                        && (j.Status == JobStatus.Running || j.Status == JobStatus.Assigned))
            .ToListAsync(ct);

        if (liveJobs.Count == 0)
            return Array.Empty<Guid>();

        var now = DateTimeOffset.UtcNow;
        foreach (var job in liveJobs)
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = now;
            job.ErrorMessage = reason;
        }

        await db.SaveChangesAsync(ct);
        return liveJobs.Select(j => j.Id).ToList();
    }

    public async Task<IReadOnlyList<Guid>> FailStuckAssignedJobsAsync(TimeSpan assignedTimeout, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - assignedTimeout;

        // Jobs stuck in Assigned: claimed by a worker but never transitioned to Running.
        // EnqueuedAt is used as the baseline because there is no separate AssignedAt timestamp.
        var stuckJobs = await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Assigned && j.StartedAt == null && j.EnqueuedAt < cutoff)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in stuckJobs)
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = now;
            job.ErrorMessage = "Timed out — worker claimed this job but never started it.";
        }

        if (stuckJobs.Count > 0)
            await db.SaveChangesAsync(ct);

        return stuckJobs.Select(j => j.Id).ToList();
    }

    // Repo caches
    public async Task<IReadOnlyList<RepoCache>> GetRepoCachesAsync(Guid workerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RepoCaches.Where(r => r.WorkerId == workerId).ToListAsync(ct);
    }

    public async Task<RepoCache?> GetRepoCacheAsync(Guid workerId, Guid projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RepoCaches.FirstOrDefaultAsync(r => r.WorkerId == workerId && r.ProjectId == projectId, ct);
    }

    public async Task UpsertRepoCacheAsync(RepoCache cache, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.RepoCaches.FirstOrDefaultAsync(
            r => r.WorkerId == cache.WorkerId && r.ProjectId == cache.ProjectId, ct);

        if (existing is null)
            db.RepoCaches.Add(cache);
        else
        {
            existing.LocalPath = cache.LocalPath;
            existing.SizeBytes = cache.SizeBytes;
            existing.LastUsedAt = cache.LastUsedAt;
            existing.LastKnownCommitSha = cache.LastKnownCommitSha;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRepoCacheAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.RepoCaches.FindAsync([id], ct);
        if (r != null)
        {
            db.RepoCaches.Remove(r);
            await db.SaveChangesAsync(ct);
        }
    }

    // Users
    public async Task<User?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.FirstOrDefaultAsync(u => u.Username == username.ToLower(), ct);
    }

    public async Task<User?> GetUserAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.FindAsync([id], ct);
    }

    public async Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.OrderBy(u => u.Username).ToListAsync(ct);
    }

    public async Task AddUserAsync(User user, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateUserAsync(User user, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Users.Update(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var u = await db.Users.FindAsync([id], ct);
        if (u != null)
        {
            db.Users.Remove(u);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> AnyUserExistsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AnyAsync(ct);
    }

    // Secrets
    public async Task<IReadOnlyList<Secret>> GetSecretsAsync(Guid? projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Secrets
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<Secret?> GetSecretAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Secrets.FindAsync([id], ct);
    }

    public async Task AddSecretAsync(Secret secret, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Secrets.Add(secret);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateSecretAsync(Secret secret, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Secrets.Update(secret);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSecretAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await db.Secrets.FindAsync([id], ct);
        if (s != null)
        {
            db.Secrets.Remove(s);
            await db.SaveChangesAsync(ct);
        }
    }
}

