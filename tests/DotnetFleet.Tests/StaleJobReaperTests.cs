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
    public async Task FailStuckAssignedJobsAsync_fails_assigned_jobs_past_timeout()
    {
        var storage = new EfFleetStorage(factory);
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
        var storage = new EfFleetStorage(factory);
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
}
