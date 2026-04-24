using DotnetFleet.Coordinator.Data;
using DotnetFleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

/// <summary>
/// Coverage for the storage primitives the push scheduler depends on:
/// work-stealing (<see cref="EfFleetStorage.TryStealAssignedJobAsync"/>) and
/// orphan reclamation (<see cref="EfFleetStorage.UnassignJobsOfOfflineWorkersAsync"/>).
/// </summary>
public class PushSchedulerStorageTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;
    private readonly EfFleetStorage storage;

    public PushSchedulerStorageTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-push-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        factory = new InlineFactory(options);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();
        storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
    }

    private sealed class InlineFactory(DbContextOptions<FleetDbContext> options)
        : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(options);
    }

    public void Dispose()
    {
        try { File.Delete(dbPath); } catch { /* best-effort */ }
    }

    private async Task<Guid> SeedProjectAsync()
    {
        var id = Guid.NewGuid();
        await storage.AddProjectAsync(new Project
        {
            Id = id, Name = "p", GitUrl = "https://example.com/p.git", Branch = "main"
        });
        return id;
    }

    [Fact]
    public async Task TryStealAssignedJobAsync_succeeds_when_current_owner_matches()
    {
        var projectId = await SeedProjectAsync();
        var owner = Guid.NewGuid();
        var thief = Guid.NewGuid();
        await storage.AddWorkerAsync(new Worker { Id = owner, Name = "owner", Status = WorkerStatus.Online });
        await storage.AddWorkerAsync(new Worker { Id = thief, Name = "thief", Status = WorkerStatus.Online });

        var jobId = Guid.NewGuid();
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId, ProjectId = projectId,
            Status = JobStatus.Queued, EnqueuedAt = DateTimeOffset.UtcNow
        });
        (await storage.AssignJobToWorkerAsync(jobId, owner, estimatedDurationMs: 60_000)).Should().BeTrue();

        var stolen = await storage.TryStealAssignedJobAsync(jobId, thief, owner, estimatedDurationMs: 5_000);
        stolen.Should().BeTrue();

        var job = await storage.GetJobAsync(jobId);
        job!.WorkerId.Should().Be(thief);
        job.EstimatedDurationMs.Should().Be(5_000);
        job.Status.Should().Be(JobStatus.Assigned);
    }

    [Fact]
    public async Task TryStealAssignedJobAsync_fails_when_current_owner_does_not_match()
    {
        var projectId = await SeedProjectAsync();
        var owner = Guid.NewGuid();
        var thief = Guid.NewGuid();
        await storage.AddWorkerAsync(new Worker { Id = owner, Name = "owner", Status = WorkerStatus.Online });
        await storage.AddWorkerAsync(new Worker { Id = thief, Name = "thief", Status = WorkerStatus.Online });

        var jobId = Guid.NewGuid();
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId, ProjectId = projectId,
            Status = JobStatus.Queued, EnqueuedAt = DateTimeOffset.UtcNow
        });
        await storage.AssignJobToWorkerAsync(jobId, owner, estimatedDurationMs: 60_000);

        // Wrong "expected current owner" — the steal must be rejected.
        var stolen = await storage.TryStealAssignedJobAsync(jobId, thief, Guid.NewGuid(), estimatedDurationMs: 1_000);
        stolen.Should().BeFalse();

        var job = await storage.GetJobAsync(jobId);
        job!.WorkerId.Should().Be(owner);
    }

    [Fact]
    public async Task TryStealAssignedJobAsync_fails_when_job_already_running()
    {
        var projectId = await SeedProjectAsync();
        var owner = Guid.NewGuid();
        var thief = Guid.NewGuid();
        await storage.AddWorkerAsync(new Worker { Id = owner, Name = "owner", Status = WorkerStatus.Online });

        var jobId = Guid.NewGuid();
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId, ProjectId = projectId,
            Status = JobStatus.Queued, EnqueuedAt = DateTimeOffset.UtcNow
        });
        await storage.AssignJobToWorkerAsync(jobId, owner, estimatedDurationMs: 60_000);

        // Move it to Running — a steal must be impossible past this point.
        var job = await storage.GetJobAsync(jobId);
        job!.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await storage.UpdateJobAsync(job);

        var stolen = await storage.TryStealAssignedJobAsync(jobId, thief, owner, estimatedDurationMs: 1_000);
        stolen.Should().BeFalse();

        var fresh = await storage.GetJobAsync(jobId);
        fresh!.WorkerId.Should().Be(owner);
        fresh.Status.Should().Be(JobStatus.Running);
    }

    [Fact]
    public async Task UnassignJobsOfOfflineWorkersAsync_returns_assigned_jobs_to_queue()
    {
        var projectId = await SeedProjectAsync();
        var dead = Guid.NewGuid();
        await storage.AddWorkerAsync(new Worker
        {
            Id = dead, Name = "dead",
            Status = WorkerStatus.Offline,
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        var jobId = Guid.NewGuid();
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId, ProjectId = projectId,
            Status = JobStatus.Queued, EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-15)
        });
        await storage.AssignJobToWorkerAsync(jobId, dead, estimatedDurationMs: 30_000);

        var reclaimed = await storage.UnassignJobsOfOfflineWorkersAsync(TimeSpan.FromMinutes(2));
        reclaimed.Should().ContainSingle().Which.Should().Be(jobId);

        var job = await storage.GetJobAsync(jobId);
        job!.Status.Should().Be(JobStatus.Queued);
        job.WorkerId.Should().BeNull();
        job.AssignedAt.Should().BeNull();
        job.EstimatedDurationMs.Should().BeNull();
    }

    [Fact]
    public async Task UnassignJobsOfOfflineWorkersAsync_leaves_running_jobs_alone()
    {
        var projectId = await SeedProjectAsync();
        var dead = Guid.NewGuid();
        await storage.AddWorkerAsync(new Worker
        {
            Id = dead, Name = "dead",
            Status = WorkerStatus.Offline,
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        var jobId = Guid.NewGuid();
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId, ProjectId = projectId,
            Status = JobStatus.Running, EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-9), WorkerId = dead
        });

        var reclaimed = await storage.UnassignJobsOfOfflineWorkersAsync(TimeSpan.FromMinutes(2));
        reclaimed.Should().BeEmpty();

        var job = await storage.GetJobAsync(jobId);
        job!.Status.Should().Be(JobStatus.Running);
        job.WorkerId.Should().Be(dead);
    }

    [Fact]
    public async Task EWMA_upsert_round_trips()
    {
        var projectId = await SeedProjectAsync();
        var workerId = Guid.NewGuid();

        (await storage.GetJobDurationStatAsync(projectId, workerId)).Should().BeNull();

        await storage.UpsertJobDurationStatAsync(projectId, workerId, newEwmaMs: 12_345.6, samples: 1);
        var first = await storage.GetJobDurationStatAsync(projectId, workerId);
        first!.EwmaMs.Should().Be(12_345.6);
        first.Samples.Should().Be(1);

        await storage.UpsertJobDurationStatAsync(projectId, workerId, newEwmaMs: 9_999.0, samples: 2);
        var second = await storage.GetJobDurationStatAsync(projectId, workerId);
        second!.EwmaMs.Should().Be(9_999.0);
        second.Samples.Should().Be(2);

        var byProject = await storage.GetJobDurationStatsForProjectAsync(projectId);
        byProject.Should().ContainSingle().Which.WorkerId.Should().Be(workerId);
    }
}
