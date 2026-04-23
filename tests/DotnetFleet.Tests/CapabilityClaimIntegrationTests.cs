using DotnetFleet.Coordinator.Data;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

/// <summary>
/// End-to-end style integration test for the capability-aware claim path. Two workers
/// register with different specs and both poll for a single queued job; only the
/// higher-spec worker is allowed to claim it. The lower-spec worker may only claim
/// after the better one becomes Busy/Offline (i.e. is no longer in the idle pool).
/// </summary>
public class CapabilityClaimIntegrationTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;
    private readonly IWorkerSelector selector = new CapabilityWorkerSelector();

    public CapabilityClaimIntegrationTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-cap-claim-{Guid.NewGuid():N}.db");
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

    private async Task<DeploymentJob> SeedJobAsync(IFleetStorage storage)
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
    public async Task High_spec_worker_wins_the_job_over_low_spec_worker()
    {
        var storage = new EfFleetStorage(factory, selector);

        var rpi = new Worker
        {
            Name = "rpi4", Status = WorkerStatus.Online,
            ProcessorCount = 4, TotalMemoryMb = 4 * 1024, Architecture = "Arm64"
        };
        var pc = new Worker
        {
            Name = "pc", Status = WorkerStatus.Online,
            ProcessorCount = 8, TotalMemoryMb = 16 * 1024, Architecture = "X64"
        };
        await storage.AddWorkerAsync(rpi);
        await storage.AddWorkerAsync(pc);

        var job = await SeedJobAsync(storage);

        // The lower-spec worker polls first — it must be told "no job for you" because
        // a better worker is also idle. The job stays Queued.
        var rpiClaim = await storage.ClaimNextJobAsync(rpi.Id);
        rpiClaim.Should().BeNull("the higher-spec PC is idle and should preempt the RPi");

        var stillQueued = await storage.GetJobAsync(job.Id);
        stillQueued!.Status.Should().Be(JobStatus.Queued);
        stillQueued.WorkerId.Should().BeNull();

        // The PC polls next — it wins the job.
        var pcClaim = await storage.ClaimNextJobAsync(pc.Id);
        pcClaim.Should().NotBeNull();
        pcClaim!.WorkerId.Should().Be(pc.Id);

        var afterClaim = await storage.GetJobAsync(job.Id);
        afterClaim!.Status.Should().Be(JobStatus.Assigned);
        afterClaim.WorkerId.Should().Be(pc.Id);
    }

    [Fact]
    public async Task Low_spec_worker_can_claim_when_high_spec_worker_is_busy()
    {
        var storage = new EfFleetStorage(factory, selector);

        var rpi = new Worker
        {
            Name = "rpi4", Status = WorkerStatus.Online,
            ProcessorCount = 4, TotalMemoryMb = 4 * 1024, Architecture = "Arm64"
        };
        var pc = new Worker
        {
            // Already running another job → Busy → not in the idle pool.
            Name = "pc", Status = WorkerStatus.Busy,
            ProcessorCount = 8, TotalMemoryMb = 16 * 1024, Architecture = "X64"
        };
        await storage.AddWorkerAsync(rpi);
        await storage.AddWorkerAsync(pc);

        await SeedJobAsync(storage);

        var rpiClaim = await storage.ClaimNextJobAsync(rpi.Id);
        rpiClaim.Should().NotBeNull("the only idle worker must be allowed to claim");
        rpiClaim!.WorkerId.Should().Be(rpi.Id);
    }

    [Fact]
    public async Task Single_idle_worker_claims_even_with_no_capabilities_reported()
    {
        var storage = new EfFleetStorage(factory, selector);

        var legacy = new Worker { Name = "legacy", Status = WorkerStatus.Online };
        await storage.AddWorkerAsync(legacy);
        await SeedJobAsync(storage);

        var claim = await storage.ClaimNextJobAsync(legacy.Id);
        claim.Should().NotBeNull();
        claim!.WorkerId.Should().Be(legacy.Id);
    }
}
