using DotnetFleet.Coordinator.Data;
using DotnetFleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

/// <summary>
/// Verifies <see cref="EfFleetStorage.FailJobsForWorkerAsync"/> — the self-heal hook
/// invoked when a worker re-announces Online (i.e. it just (re)started). Any leftover
/// Assigned/Running jobs owned by that worker must be failed in one shot so the worker
/// is not blocked from claiming new work.
/// </summary>
public class FailJobsForWorkerTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public FailJobsForWorkerTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-self-heal-{Guid.NewGuid():N}.db");
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
    public async Task Fails_running_and_assigned_jobs_for_worker_and_leaves_terminal_ones_alone()
    {
        var storage = new EfFleetStorage(factory);
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var otherWorkerId = Guid.NewGuid();

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });

        var running = new DeploymentJob
        {
            Id = Guid.NewGuid(), ProjectId = projectId, WorkerId = workerId,
            Status = JobStatus.Running, EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var assigned = new DeploymentJob
        {
            Id = Guid.NewGuid(), ProjectId = projectId, WorkerId = workerId,
            Status = JobStatus.Assigned, EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var succeeded = new DeploymentJob
        {
            Id = Guid.NewGuid(), ProjectId = projectId, WorkerId = workerId,
            Status = JobStatus.Succeeded, EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var otherRunning = new DeploymentJob
        {
            Id = Guid.NewGuid(), ProjectId = projectId, WorkerId = otherWorkerId,
            Status = JobStatus.Running, EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await storage.AddJobAsync(running);
        await storage.AddJobAsync(assigned);
        await storage.AddJobAsync(succeeded);
        await storage.AddJobAsync(otherRunning);

        var failed = await storage.FailJobsForWorkerAsync(workerId, "Worker restarted while running this job.");

        failed.Should().BeEquivalentTo(new[] { running.Id, assigned.Id });

        (await storage.GetJobAsync(running.Id))!.Status.Should().Be(JobStatus.Failed);
        (await storage.GetJobAsync(assigned.Id))!.Status.Should().Be(JobStatus.Failed);
        (await storage.GetJobAsync(running.Id))!.ErrorMessage.Should().Be("Worker restarted while running this job.");

        // Untouched
        (await storage.GetJobAsync(succeeded.Id))!.Status.Should().Be(JobStatus.Succeeded);
        (await storage.GetJobAsync(otherRunning.Id))!.Status.Should().Be(JobStatus.Running);
    }

    [Fact]
    public async Task Returns_empty_when_worker_has_no_live_jobs()
    {
        var storage = new EfFleetStorage(factory);
        var workerId = Guid.NewGuid();

        var failed = await storage.FailJobsForWorkerAsync(workerId, "anything");

        failed.Should().BeEmpty();
    }
}
