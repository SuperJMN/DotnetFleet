using DotnetFleet.Coordinator.Data;
using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetFleet.Tests;

/// <summary>
/// End-to-end tests for the push-based scheduler. Drives
/// <see cref="JobAssignmentService.AssignPendingAsync"/> directly against a real
/// SQLite-backed <see cref="EfFleetStorage"/> and verifies the assigner's picks.
/// </summary>
public class CapabilityClaimIntegrationTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;
    private readonly IWorkerSelector selector = new CapabilityWorkerSelector();
    private readonly ServiceProvider services;
    private readonly JobAssignmentService assigner;
    private readonly IFleetStorage storage;

    public CapabilityClaimIntegrationTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-cap-claim-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        factory = new InlineFactory(options);

        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        var sc = new ServiceCollection();
        sc.AddSingleton<IDbContextFactory<FleetDbContext>>(factory);
        sc.AddSingleton<IWorkerSelector>(selector);
        sc.AddSingleton<IFleetStorage, EfFleetStorage>();
        sc.AddSingleton<IDurationEstimator, EwmaDurationEstimator>();
        sc.AddSingleton<JobAssignmentSignal>();
        sc.AddSingleton<LogBroadcaster>();
        sc.AddSingleton<JobAssignmentService>(sp => new JobAssignmentService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<JobAssignmentSignal>(),
            sp.GetRequiredService<LogBroadcaster>(),
            NullLogger<JobAssignmentService>.Instance));
        services = sc.BuildServiceProvider();
        assigner = services.GetRequiredService<JobAssignmentService>();
        storage = services.GetRequiredService<IFleetStorage>();
    }

    private sealed class InlineFactory(DbContextOptions<FleetDbContext> options)
        : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(options);
    }

    public void Dispose()
    {
        services.Dispose();
        try { File.Delete(dbPath); } catch { /* best-effort */ }
    }

    private async Task<DeploymentJob> SeedJobAsync()
    {
        var projectId = Guid.NewGuid();
        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(), ProjectId = projectId,
            Status = JobStatus.Queued, EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);
        return job;
    }

    [Fact]
    public async Task Single_idle_worker_gets_the_job_when_no_history_exists()
    {
        var w = new Worker { Name = "only", Status = WorkerStatus.Online };
        await storage.AddWorkerAsync(w);
        var job = await SeedJobAsync();

        await assigner.AssignPendingAsync(CancellationToken.None);

        var after = await storage.GetJobAsync(job.Id);
        after!.Status.Should().Be(JobStatus.Assigned);
        after.WorkerId.Should().Be(w.Id);
    }

    [Fact]
    public async Task Loaded_worker_is_skipped_in_favor_of_idle_one()
    {
        // Two compatible workers; one already has an Assigned job (queue depth > 0),
        // the other is idle. Idle worker has lower ETA → wins the new job.
        var loaded = new Worker { Name = "loaded", Status = WorkerStatus.Online };
        var idle = new Worker { Name = "idle", Status = WorkerStatus.Online };
        await storage.AddWorkerAsync(loaded);
        await storage.AddWorkerAsync(idle);

        // Seed a long-running job already assigned to the loaded worker.
        var preexisting = await SeedJobAsync();
        await storage.AssignJobToWorkerAsync(preexisting.Id, loaded.Id, estimatedDurationMs: 600_000);

        var job = await SeedJobAsync();
        await assigner.AssignPendingAsync(CancellationToken.None);

        var after = await storage.GetJobAsync(job.Id);
        after!.Status.Should().Be(JobStatus.Assigned);
        after.WorkerId.Should().Be(idle.Id);
    }

    [Fact]
    public async Task Job_remains_queued_when_no_workers_exist()
    {
        var job = await SeedJobAsync();

        await assigner.AssignPendingAsync(CancellationToken.None);

        var after = await storage.GetJobAsync(job.Id);
        after!.Status.Should().Be(JobStatus.Queued);
        after.WorkerId.Should().BeNull();
    }

    [Fact]
    public async Task Faster_worker_per_history_is_preferred_when_both_idle()
    {
        // Both workers idle, both compatible, but project history says worker A is much
        // faster on this project. Assigner must pick A.
        var fast = new Worker { Name = "fast", Status = WorkerStatus.Online,
            ProcessorCount = 8, TotalMemoryMb = 16 * 1024 };
        var slow = new Worker { Name = "slow", Status = WorkerStatus.Online,
            ProcessorCount = 2, TotalMemoryMb = 1024 };
        await storage.AddWorkerAsync(fast);
        await storage.AddWorkerAsync(slow);

        var job = await SeedJobAsync();
        await storage.UpsertJobDurationStatAsync(job.ProjectId, fast.Id, newEwmaMs: 5_000, samples: 5);
        await storage.UpsertJobDurationStatAsync(job.ProjectId, slow.Id, newEwmaMs: 60_000, samples: 5);

        await assigner.AssignPendingAsync(CancellationToken.None);

        var after = await storage.GetJobAsync(job.Id);
        after!.WorkerId.Should().Be(fast.Id);
    }
}
