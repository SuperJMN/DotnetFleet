using System.Reflection;
using System.Security.Claims;
using DotnetFleet.Coordinator.Data;
using DotnetFleet.Coordinator.Endpoints;
using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

public class JobLifecycleRaceTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public JobLifecycleRaceTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-job-race-{Guid.NewGuid():N}.db");
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
    public async Task ReportStarted_WhenJobIsAlreadyTerminal_ShouldNotMoveItBackToRunning()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await SeedTerminalJob(storage, projectId, workerId, jobId);

        var result = await InvokeReportStarted(jobId, workerId, storage);

        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status409Conflict);
        (await storage.GetJobAsync(jobId))!.Status.Should().Be(JobStatus.Cancelled);
    }

    [Fact]
    public async Task ReportCompleted_WhenJobIsAlreadyTerminal_ShouldNotOverwriteStatus()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await SeedTerminalJob(storage, projectId, workerId, jobId);

        var result = await InvokeReportCompleted(jobId, workerId, storage);

        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status409Conflict);
        (await storage.GetJobAsync(jobId))!.Status.Should().Be(JobStatus.Cancelled);
    }

    private static async Task SeedTerminalJob(
        EfFleetStorage storage,
        Guid projectId,
        Guid workerId,
        Guid jobId)
    {
        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId,
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Cancelled,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationRequestedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
    }

    private static async Task<IResult> InvokeReportStarted(Guid jobId, Guid workerId, EfFleetStorage storage)
    {
        var method = typeof(JobEndpoints).GetMethod(
            "ReportStarted",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = (Task<IResult>)method.Invoke(null, new object[]
        {
            jobId,
            CreateWorkerContext(workerId),
            storage
        })!;
        return await task;
    }

    private static async Task<IResult> InvokeReportCompleted(Guid jobId, Guid workerId, EfFleetStorage storage)
    {
        var method = typeof(JobEndpoints).GetMethod(
            "ReportCompleted",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = (Task<IResult>)method.Invoke(null, new object[]
        {
            jobId,
            new JobEndpoints.CompleteJobRequest(true, null),
            CreateWorkerContext(workerId),
            storage,
            new LogBroadcaster(),
            new JobAssignmentSignal()
        })!;
        return await task;
    }

    private static HttpContext CreateWorkerContext(Guid workerId) =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("worker_id", workerId.ToString())
            }, "test"))
        };
}
