using System.Reflection;
using System.Security.Claims;
using DotnetFleet.Coordinator.Data;
using DotnetFleet.Coordinator.Endpoints;
using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

public class WorkerStatusEndpointTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public WorkerStatusEndpointTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-worker-status-{Guid.NewGuid():N}.db");
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
    public async Task UpdateStatus_WhenWorkerReportsOnline_ShouldFailRunningJobButKeepAssignedJobs()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        var assignedId = Guid.NewGuid();

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        await storage.AddWorkerAsync(new Worker
        {
            Id = workerId,
            Name = "w",
            Status = WorkerStatus.Busy
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = runningId,
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Running,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
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

        var result = await InvokeUpdateStatus(workerId, storage);

        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status200OK);

        (await storage.GetJobAsync(runningId))!.Status.Should().Be(JobStatus.Failed);
        (await storage.GetJobAsync(assignedId))!.Status.Should().Be(JobStatus.Assigned);
    }

    private static async Task<IResult> InvokeUpdateStatus(Guid workerId, EfFleetStorage storage)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("worker_id", workerId.ToString())
            }, "test"))
        };

        var method = typeof(WorkerEndpoints).GetMethod(
            "UpdateStatus",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = (Task<IResult>)method.Invoke(null, new object[]
        {
            workerId,
            new WorkerEndpoints.UpdateWorkerStatusRequest(WorkerStatus.Online),
            httpContext,
            storage,
            new LogBroadcaster(),
            new JobAssignmentSignal()
        })!;
        return await task;
    }
}
