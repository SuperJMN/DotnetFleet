using System.Reflection;
using System.Text;
using DotnetFleet.Coordinator.Data;
using DotnetFleet.Coordinator.Endpoints;
using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DotnetFleet.Tests;

public sealed class JobEndpointsFinishedJobsTests : IDisposable
{
    private readonly string dbPath;
    private readonly string artifactsRoot;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public JobEndpointsFinishedJobsTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-global-finished-jobs-{Guid.NewGuid():N}.db");
        artifactsRoot = Path.Combine(Path.GetTempPath(), $"fleet-global-finished-artifacts-{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        factory = new InlineFactory(options);

        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public void MapJobEndpoints_ShouldExposeGlobalFinishedJobsDeleteEndpoint()
    {
        var source = File.ReadAllText(SourceFilePath("DotnetFleet.Coordinator", "Endpoints", "JobEndpoints.cs"));

        source.Should().Contain("group.MapDelete(\"/finished\", DeleteFinishedJobs)");
    }

    [Fact]
    public async Task DeleteFinishedJobs_WhenInvokedGlobally_ShouldDeleteTerminalJobsAndArtifactsOnly()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var artifacts = new PackageArtifactStore(artifactsRoot);
        var firstProjectId = Guid.NewGuid();
        var secondProjectId = Guid.NewGuid();
        var finishedPackage = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = firstProjectId,
            Kind = JobKind.PackageBuild,
            Status = JobStatus.Succeeded,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var failedDeploy = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = secondProjectId,
            Status = JobStatus.Failed,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        };
        var runningPackage = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = firstProjectId,
            Kind = JobKind.PackageBuild,
            Status = JobStatus.Running,
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        await storage.AddProjectAsync(new Project
        {
            Id = firstProjectId, Name = "first", GitUrl = "https://example.test/first.git", Branch = "main"
        });
        await storage.AddProjectAsync(new Project
        {
            Id = secondProjectId, Name = "second", GitUrl = "https://example.test/second.git", Branch = "main"
        });
        await storage.AddJobAsync(finishedPackage);
        await storage.AddJobAsync(failedDeploy);
        await storage.AddJobAsync(runningPackage);
        await using (var content = new MemoryStream(Encoding.UTF8.GetBytes("payload")))
            await artifacts.SaveAsync(finishedPackage, "linux/package.deb", content);
        await using (var content = new MemoryStream(Encoding.UTF8.GetBytes("live")))
            await artifacts.SaveAsync(runningPackage, "linux/live.deb", content);

        var result = await InvokeDeleteFinishedJobs(storage, artifacts);

        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status200OK);
        (await storage.GetJobsAsync()).Should().ContainSingle(job => job.Id == runningPackage.Id);
        (await artifacts.ListAsync(finishedPackage)).Should().BeEmpty();
        (await artifacts.ListAsync(runningPackage)).Should().ContainSingle(artifact => artifact.RelativePath == "linux/live.deb");
    }

    private static async Task<IResult> InvokeDeleteFinishedJobs(IFleetStorage storage, PackageArtifactStore artifacts)
    {
        var method = typeof(JobEndpoints).GetMethod(
            "DeleteFinishedJobs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = method.GetParameters()
            .Select(parameter => ResolveParameter(parameter.ParameterType, storage, artifacts))
            .ToArray();

        var task = (Task<IResult>)method.Invoke(null, args)!;
        return await task;
    }

    private static object ResolveParameter(
        Type parameterType,
        IFleetStorage storage,
        PackageArtifactStore artifacts)
    {
        if (parameterType == typeof(IFleetStorage)) return storage;
        if (parameterType == typeof(PackageArtifactStore)) return artifacts;

        throw new InvalidOperationException($"Unsupported DeleteFinishedJobs parameter type {parameterType}.");
    }

    private sealed class InlineFactory(DbContextOptions<FleetDbContext> options)
        : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(options);
    }

    private static string SourceFilePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var pathParts = new[] { directory.FullName, "src" }.Concat(parts).ToArray();
            var path = Path.Combine(pathParts);
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(parts)} from the test output directory.");
    }

    public void Dispose()
    {
        try { File.Delete(dbPath); } catch { /* best-effort */ }
        try { Directory.Delete(artifactsRoot, recursive: true); } catch { /* best-effort */ }
    }
}
