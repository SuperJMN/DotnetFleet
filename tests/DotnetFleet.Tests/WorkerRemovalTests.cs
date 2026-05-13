using System.Reflection;
using DotnetFleet.Coordinator.Data;
using DotnetFleet.Coordinator.Endpoints;
using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

public class WorkerRemovalTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public WorkerRemovalTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-worker-removal-{Guid.NewGuid():N}.db");
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
    public async Task DeleteWorkerEndpoint_WhenWorkerExists_ShouldRemoveWorkerAndFailLiveJobs()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        var assignedId = Guid.NewGuid();
        var succeededId = Guid.NewGuid();

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        await storage.AddWorkerAsync(new Worker
        {
            Id = workerId,
            Name = "DESKTOP-NMC4AGI",
            Status = WorkerStatus.Offline
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = runningId,
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Running,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = assignedId,
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Assigned,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = succeededId,
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Succeeded,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-4)
        });
        await storage.UpsertRepoCacheAsync(new RepoCache
        {
            WorkerId = workerId,
            ProjectId = projectId,
            LocalPath = "cache",
            SizeBytes = 42,
            LastUsedAt = DateTimeOffset.UtcNow
        });
        await storage.UpsertJobDurationStatAsync(projectId, workerId, 1234, 1);

        var result = await InvokeDeleteWorker(workerId, storage);

        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status200OK);

        (await storage.GetWorkerAsync(workerId)).Should().BeNull();
        (await storage.GetJobAsync(runningId))!.Status.Should().Be(JobStatus.Failed);
        (await storage.GetJobAsync(assignedId))!.Status.Should().Be(JobStatus.Failed);
        (await storage.GetJobAsync(succeededId))!.Status.Should().Be(JobStatus.Succeeded);
        (await storage.GetRepoCachesAsync(workerId)).Should().BeEmpty();
        (await storage.GetJobDurationStatAsync(projectId, workerId)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteWorkerEndpoint_WhenWorkerDoesNotExist_ShouldReturnNotFound()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());

        var result = await InvokeDeleteWorker(Guid.NewGuid(), storage);

        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> InvokeDeleteWorker(Guid workerId, EfFleetStorage storage)
    {
        var method = typeof(WorkerEndpoints).GetMethod(
            "Delete",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var task = (Task<IResult>)method!.Invoke(null, new object[]
        {
            workerId,
            storage,
            new LogBroadcaster()
        })!;
        return await task;
    }
}
