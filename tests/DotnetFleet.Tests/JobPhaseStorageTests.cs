using DotnetFleet.Coordinator.Data;
using DotnetFleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

public class JobPhaseStorageTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public JobPhaseStorageTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-phases-{Guid.NewGuid():N}.db");
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
    public async Task Ending_nested_phase_restores_parent_then_clears_current_phase()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        await storage.AddProjectAsync(new Project
        {
            Id = projectId,
            Name = "p",
            GitUrl = "https://example.com/r.git",
            Branch = "main"
        });

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = JobStatus.Running,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        var start = DateTimeOffset.UtcNow;
        await storage.RecordJobPhaseAsync(job.Id, new PhaseEvent
        {
            Kind = PhaseEventKind.Start,
            Name = "worker.deployer.invoke"
        }, start);
        await storage.RecordJobPhaseAsync(job.Id, new PhaseEvent
        {
            Kind = PhaseEventKind.Start,
            Name = "nuget.pack"
        }, start.AddSeconds(1));

        await storage.RecordJobPhaseAsync(job.Id, new PhaseEvent
        {
            Kind = PhaseEventKind.End,
            Name = "nuget.pack",
            Status = PhaseStatus.Ok
        }, start.AddSeconds(2));

        var afterInner = await storage.GetJobAsync(job.Id);
        afterInner!.CurrentPhase.Should().Be("worker.deployer.invoke");
        afterInner.CurrentPhaseStartedAt.Should().BeCloseTo(start, TimeSpan.FromMilliseconds(1));

        await storage.RecordJobPhaseAsync(job.Id, new PhaseEvent
        {
            Kind = PhaseEventKind.End,
            Name = "worker.deployer.invoke",
            Status = PhaseStatus.Ok
        }, start.AddSeconds(3));

        var afterParent = await storage.GetJobAsync(job.Id);
        afterParent!.CurrentPhase.Should().BeNull();
        afterParent.CurrentPhaseStartedAt.Should().BeNull();
    }
}
