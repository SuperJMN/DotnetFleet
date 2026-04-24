using DotnetFleet.Coordinator.Data;
using DotnetFleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

/// <summary>
/// Verifies <see cref="EfFleetStorage.AssignJobToWorkerAsync"/> is atomic under
/// concurrent callers: when N workers race to claim a single Queued job, exactly one
/// wins. Uses a real Sqlite file so the EF Core ExecuteUpdateAsync path actually
/// exercises the WHERE Status==Queued AND WorkerId==null guard.
/// </summary>
public class EfFleetStorageClaimRaceTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public EfFleetStorageClaimRaceTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-claim-{Guid.NewGuid():N}.db");
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
    public async Task Single_queued_job_is_assigned_to_exactly_one_worker_under_contention()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "race", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        var jobId = Guid.NewGuid();
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId, ProjectId = projectId,
            Status = JobStatus.Queued, EnqueuedAt = DateTimeOffset.UtcNow
        });

        const int contenders = 16;
        var workerIds = Enumerable.Range(0, contenders).Select(_ => Guid.NewGuid()).ToArray();

        // Gate so all callers attempt the assignment simultaneously.
        using var gate = new SemaphoreSlim(0, contenders);
        var tasks = workerIds.Select(async wid =>
        {
            await gate.WaitAsync();
            return await storage.AssignJobToWorkerAsync(jobId, wid, estimatedDurationMs: null);
        }).ToArray();

        gate.Release(contenders);
        var results = await Task.WhenAll(tasks);

        results.Count(ok => ok).Should().Be(1, "only one worker may win a single queued job");

        var jobs = await storage.GetJobsAsync();
        jobs.Should().ContainSingle(j => j.Status == JobStatus.Assigned);
        jobs.Single().WorkerId.Should().NotBeNull();
    }

    [Fact]
    public async Task GetNextAssignedJobForWorkerAsync_returns_null_when_queue_is_empty()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var result = await storage.GetNextAssignedJobForWorkerAsync(Guid.NewGuid());
        result.Should().BeNull();
    }
}
