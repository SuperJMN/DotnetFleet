using DotnetFleet.Coordinator.Data;
using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

/// <summary>
/// End-to-end-ish coverage for the deployment-rename feature: pumps fake worker log
/// lines into the same pipeline the HTTP <c>AppendLogs</c> endpoint uses, and asserts
/// that the persisted <see cref="DeploymentJob"/> gets its <see cref="DeploymentJob.Version"/>
/// populated the moment a GitVersion-style line arrives — and only once.
/// </summary>
public class DeploymentVersionTrackerTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public DeploymentVersionTrackerTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-version-{Guid.NewGuid():N}.db");
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

    private async Task<DeploymentJob> SeedJobAsync(EfFleetStorage storage)
    {
        var projectId = Guid.NewGuid();
        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(), ProjectId = projectId,
            Status = JobStatus.Running, EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);
        return job;
    }

    [Fact]
    public async Task First_GitVersion_line_in_the_stream_renames_the_deployment()
    {
        var storage = new EfFleetStorage(factory);
        var job = await SeedJobAsync(storage);

        var lines = new[]
        {
            "Restoring packages...",
            "Running GitVersion...",
            "FullSemVer: 1.4.2-beta.7+build.91",
            "Build succeeded."
        };

        var detected = await DeploymentVersionTracker.TryUpdateVersionAsync(storage, job, lines);

        detected.Should().Be("1.4.2-beta.7+build.91");

        var reloaded = await storage.GetJobAsync(job.Id);
        reloaded!.Version.Should().Be("1.4.2-beta.7+build.91");
    }

    [Fact]
    public async Task Subsequent_batches_do_not_overwrite_the_first_detected_version()
    {
        var storage = new EfFleetStorage(factory);
        var job = await SeedJobAsync(storage);

        await DeploymentVersionTracker.TryUpdateVersionAsync(storage, job,
            new[] { "InformationalVersion: 1.0.0" });

        // Reload as the endpoint would on the next batch.
        var refreshed = (await storage.GetJobAsync(job.Id))!;
        var second = await DeploymentVersionTracker.TryUpdateVersionAsync(storage, refreshed,
            new[] { "FullSemVer: 9.9.9" });

        second.Should().BeNull("the job is already named, GitVersion output later in the stream must not rewrite it");
        var reloaded = await storage.GetJobAsync(job.Id);
        reloaded!.Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task Lines_without_a_version_leave_the_job_unchanged()
    {
        var storage = new EfFleetStorage(factory);
        var job = await SeedJobAsync(storage);

        var detected = await DeploymentVersionTracker.TryUpdateVersionAsync(storage, job,
            new[] { "starting build", "compiling sources", "tests passed" });

        detected.Should().BeNull();
        var reloaded = await storage.GetJobAsync(job.Id);
        reloaded!.Version.Should().BeNull();
    }
}
