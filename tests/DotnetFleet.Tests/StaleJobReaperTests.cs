using DotnetFleet.Coordinator.Data;
using DotnetFleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

/// <summary>
/// Tests for the stale/stuck job reaping storage methods to verify
/// that orphaned Assigned/Queued jobs are eventually cleaned up.
/// </summary>
public class StaleJobReaperTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public StaleJobReaperTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-reaper-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        factory = new InlineFactory(options);

        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
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

    [Fact]
    public async Task Assigned_jobs_on_stale_workers_are_requeued_instead_of_failed()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "test", GitUrl = "https://example.com/r.git", Branch = "main"
        });

        await storage.AddWorkerAsync(new Worker
        {
            Id = workerId,
            Name = "stale-worker",
            Status = WorkerStatus.Online,
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        var assignedJob = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Assigned,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await storage.AddJobAsync(assignedJob);

        await storage.MarkOfflineWorkersAsync(TimeSpan.FromSeconds(90));
        var failedIds = await storage.FailStaleJobsAsync(TimeSpan.FromSeconds(90));
        var reclaimedIds = await storage.UnassignJobsOfOfflineWorkersAsync(TimeSpan.FromSeconds(90));

        failedIds.Should().BeEmpty();
        reclaimedIds.Should().ContainSingle().Which.Should().Be(assignedJob.Id);

        var job = await storage.GetJobAsync(assignedJob.Id);
        job!.Status.Should().Be(JobStatus.Queued);
        job.WorkerId.Should().BeNull();
        job.AssignedAt.Should().BeNull();
        job.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Running_jobs_on_stale_workers_are_failed()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "test", GitUrl = "https://example.com/r.git", Branch = "main"
        });

        await storage.AddWorkerAsync(new Worker
        {
            Id = workerId,
            Name = "stale-worker",
            Status = WorkerStatus.Busy,
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        var runningJob = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Running,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4)
        };
        await storage.AddJobAsync(runningJob);

        var failedIds = await storage.FailStaleJobsAsync(TimeSpan.FromSeconds(90));

        failedIds.Should().ContainSingle().Which.Should().Be(runningJob.Id);

        var job = await storage.GetJobAsync(runningJob.Id);
        job!.Status.Should().Be(JobStatus.Failed);
        job.ErrorMessage.Should().Contain("Worker unresponsive");
    }

    [Fact]
    public async Task FailStuckAssignedJobsAsync_fails_assigned_jobs_past_timeout()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "test", GitUrl = "https://example.com/r.git", Branch = "main"
        });

        // Assigned job created 10 minutes ago (past the 5-minute timeout)
        var stuckJob = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Assigned,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            StartedAt = null
        };
        await storage.AddJobAsync(stuckJob);

        // Recent Assigned job (should NOT be reaped)
        var recentJob = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Assigned,
            EnqueuedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            StartedAt = null
        };
        await storage.AddJobAsync(recentJob);

        var failedIds = await storage.FailStuckAssignedJobsAsync(TimeSpan.FromMinutes(5));

        failedIds.Should().ContainSingle().Which.Should().Be(stuckJob.Id);

        var job = await storage.GetJobAsync(stuckJob.Id);
        job!.Status.Should().Be(JobStatus.Failed);
        job.ErrorMessage.Should().Contain("never started");

        var recent = await storage.GetJobAsync(recentJob.Id);
        recent!.Status.Should().Be(JobStatus.Assigned);
    }

    [Fact]
    public async Task FailStuckAssignedJobsAsync_ignores_running_jobs()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "test2", GitUrl = "https://example.com/r.git", Branch = "main"
        });

        // Running job with StartedAt set — should NOT be reaped
        var runningJob = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            WorkerId = Guid.NewGuid(),
            Status = JobStatus.Running,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-9)
        };
        await storage.AddJobAsync(runningJob);

        var failedIds = await storage.FailStuckAssignedJobsAsync(TimeSpan.FromMinutes(5));
        failedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task TouchWorkerAsync_updates_LastSeenAt_and_revives_offline_worker()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var workerId = Guid.NewGuid();

        await storage.AddWorkerAsync(new Worker
        {
            Id = workerId,
            Name = "rpi4",
            Status = WorkerStatus.Offline,
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        // SQLite's text-roundtripped DateTimeOffset can lose a few microseconds
        // of precision, so allow a small tolerance instead of strict ordering.
        var before = DateTimeOffset.UtcNow;
        var existed = await storage.TouchWorkerAsync(workerId);
        existed.Should().BeTrue();

        var refreshed = await storage.GetWorkerAsync(workerId);
        refreshed!.LastSeenAt.Should().NotBeNull();
        refreshed.LastSeenAt!.Value.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
        refreshed.Status.Should().Be(WorkerStatus.Online,
            "TouchWorkerAsync must revive a worker that was previously declared Offline");
    }

    [Fact]
    public async Task TouchWorkerAsync_preserves_busy_status()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var workerId = Guid.NewGuid();

        await storage.AddWorkerAsync(new Worker
        {
            Id = workerId,
            Name = "busy-worker",
            Status = WorkerStatus.Busy,
            LastSeenAt = DateTimeOffset.UtcNow.AddSeconds(-5)
        });

        await storage.TouchWorkerAsync(workerId);

        var refreshed = await storage.GetWorkerAsync(workerId);
        refreshed!.Status.Should().Be(WorkerStatus.Busy,
            "Touch must only flip Offline→Online; other statuses are caller-managed");
    }

    [Fact]
    public async Task TouchWorkerAsync_returns_false_for_unknown_worker()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var existed = await storage.TouchWorkerAsync(Guid.NewGuid());
        existed.Should().BeFalse();
    }
}
