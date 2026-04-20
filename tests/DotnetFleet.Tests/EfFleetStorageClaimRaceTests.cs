using DotnetFleet.Coordinator.Data;
using DotnetFleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

/// <summary>
/// Verifies <see cref="EfFleetStorage.ClaimNextJobAsync"/> is atomic under concurrent
/// callers: when N workers race for a single Queued job, exactly one wins.
/// Uses a real Sqlite file so the Serializable / BEGIN IMMEDIATE locking actually
/// kicks in (in-memory shared connections would not exercise the same path).
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
    public async Task Single_queued_job_is_claimed_by_exactly_one_worker_under_contention()
    {
        var storage = new EfFleetStorage(factory);
        var projectId = Guid.NewGuid();

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "race", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = Guid.NewGuid(), ProjectId = projectId,
            Status = JobStatus.Queued, EnqueuedAt = DateTimeOffset.UtcNow
        });

        const int contenders = 16;
        var workerIds = Enumerable.Range(0, contenders).Select(_ => Guid.NewGuid()).ToArray();

        // Gate so all callers attempt the claim simultaneously.
        using var gate = new SemaphoreSlim(0, contenders);
        var tasks = workerIds.Select(async wid =>
        {
            await gate.WaitAsync();
            return await storage.ClaimNextJobAsync(wid);
        }).ToArray();

        gate.Release(contenders);
        var results = await Task.WhenAll(tasks);

        var claimed = results.Where(r => r is not null).ToList();
        claimed.Should().HaveCount(1, "only one worker may win a single queued job");

        var jobs = await storage.GetJobsAsync();
        jobs.Should().ContainSingle(j => j.Status == JobStatus.Assigned);
        jobs.Single().WorkerId.Should().Be(claimed[0]!.WorkerId);
    }

    [Fact]
    public async Task ClaimNextJobAsync_returns_null_when_queue_is_empty()
    {
        var storage = new EfFleetStorage(factory);
        var result = await storage.ClaimNextJobAsync(Guid.NewGuid());
        result.Should().BeNull();
    }
}
