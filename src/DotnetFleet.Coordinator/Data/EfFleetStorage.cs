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

    public async Task<DeploymentJob?> DequeueNextJobAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Queued)
            .OrderBy(j => j.EnqueuedAt)
            .FirstOrDefaultAsync(ct);
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
}
