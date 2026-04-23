using DotnetFleet.Coordinator.Data;
using DotnetFleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

/// <summary>
/// Verifies <see cref="EfFleetStorage.SetJobVersionIfUnsetAsync"/> behaves as a
/// first-writer-wins atomic primitive: the underlying SQL is a single UPDATE
/// guarded by <c>WHERE Id = @id AND Version IS NULL</c>, so concurrent callers
/// cannot trample each other's writes.
/// Uses a real Sqlite file so the per-row write lock actually serializes the
/// updates (an in-memory shared connection would not exercise the same path).
/// </summary>
public class EfFleetStorageSetJobVersionIfUnsetTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public EfFleetStorageSetJobVersionIfUnsetTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-setversion-{Guid.NewGuid():N}.db");
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

    private async Task<DeploymentJob> SeedJobAsync(EfFleetStorage storage, string? version = null)
    {
        var projectId = Guid.NewGuid();
        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(), ProjectId = projectId,
            Status = JobStatus.Running, EnqueuedAt = DateTimeOffset.UtcNow,
            Version = version
        };
        await storage.AddJobAsync(job);
        return job;
    }

    [Fact]
    public async Task SetJobVersionIfUnset_only_first_concurrent_writer_wins()
    {
        var storage = new EfFleetStorage(factory, new DotnetFleet.Core.Domain.CapabilityWorkerSelector());
        var job = await SeedJobAsync(storage);

        const int contenders = 16;
        var candidates = Enumerable.Range(0, contenders).Select(i => $"v{i}").ToArray();

        using var gate = new SemaphoreSlim(0, contenders);
        var tasks = candidates.Select(async v =>
        {
            await gate.WaitAsync();
            return (Version: v, Won: await storage.SetJobVersionIfUnsetAsync(job.Id, v));
        }).ToArray();

        gate.Release(contenders);
        var results = await Task.WhenAll(tasks);

        var winners = results.Where(r => r.Won).ToList();
        winners.Should().HaveCount(1, "first-writer-wins: only one concurrent caller may set the version");

        var reloaded = await storage.GetJobAsync(job.Id);
        reloaded!.Version.Should().Be(winners[0].Version,
            "the persisted version must match exactly the candidate the winning caller submitted");
    }

    [Fact]
    public async Task SetJobVersionIfUnset_returns_false_when_already_set()
    {
        var storage = new EfFleetStorage(factory, new DotnetFleet.Core.Domain.CapabilityWorkerSelector());
        var job = await SeedJobAsync(storage, version: "1.0.0");

        var ok = await storage.SetJobVersionIfUnsetAsync(job.Id, "9.9.9");

        ok.Should().BeFalse();
        var reloaded = await storage.GetJobAsync(job.Id);
        reloaded!.Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task SetJobVersionIfUnset_returns_false_for_unknown_job()
    {
        var storage = new EfFleetStorage(factory, new DotnetFleet.Core.Domain.CapabilityWorkerSelector());
        var ok = await storage.SetJobVersionIfUnsetAsync(Guid.NewGuid(), "1.0.0");
        ok.Should().BeFalse();
    }
}
