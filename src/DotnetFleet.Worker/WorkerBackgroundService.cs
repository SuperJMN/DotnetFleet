using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using DotnetFleet.WorkerService.Execution;
using DotnetFleet.WorkerService.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetFleet.WorkerService;

/// <summary>
/// Background service that polls for deployment jobs and executes them.
/// Can run embedded inside the Coordinator or as a standalone process.
/// </summary>
public class WorkerBackgroundService : BackgroundService
{
    private readonly IWorkerJobSource jobSource;
    private readonly IFleetStorage storage;
    private readonly IConfiguration config;
    private readonly ILogger<WorkerBackgroundService> logger;

    private Guid workerId;
    private string repoStoragePath = "fleet-repos";
    private TimeSpan heartbeatInterval;
    private TimeSpan pollInterval;

    public WorkerBackgroundService(
        IWorkerJobSource jobSource,
        IFleetStorage storage,
        IConfiguration config,
        ILogger<WorkerBackgroundService> logger)
    {
        this.jobSource = jobSource;
        this.storage = storage;
        this.config = config;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        heartbeatInterval = TimeSpan.FromSeconds(
            config.GetValue("Worker:HeartbeatIntervalSeconds", 30));
        pollInterval = TimeSpan.FromSeconds(
            config.GetValue("Worker:PollIntervalSeconds", 10));
        repoStoragePath = config["Worker:RepoStoragePath"] ?? "fleet-repos";

        if (!Guid.TryParse(config["Worker:EmbeddedWorkerId"], out workerId))
        {
            logger.LogWarning("Worker:EmbeddedWorkerId not configured; worker will not execute jobs.");
            return;
        }

        Directory.CreateDirectory(repoStoragePath);
        logger.LogInformation("Worker {Id} started. Repos stored in {Path}", workerId, repoStoragePath);

        using var heartbeatTimer = new PeriodicTimer(heartbeatInterval);
        using var pollTimer = new PeriodicTimer(pollInterval);

        var heartbeatTask = HeartbeatLoopAsync(heartbeatTimer, stoppingToken);
        var pollTask = PollLoopAsync(pollTimer, stoppingToken);

        await Task.WhenAll(heartbeatTask, pollTask);
    }

    private async Task HeartbeatLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var worker = await storage.GetWorkerAsync(workerId, ct);
                if (worker is null) continue;
                worker.LastSeenAt = DateTimeOffset.UtcNow;
                if (worker.Status == WorkerStatus.Offline)
                    worker.Status = WorkerStatus.Online;
                await storage.UpdateWorkerAsync(worker, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Heartbeat failed");
            }
        }
    }

    private async Task PollLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var job = await jobSource.GetNextJobAsync(workerId, ct);
                if (job is null) continue;

                await ExecuteJobAsync(job, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in poll loop");
            }
        }
    }

    private async Task ExecuteJobAsync(DeploymentJob job, CancellationToken ct)
    {
        logger.LogInformation("Starting job {JobId} for project {ProjectId}", job.Id, job.ProjectId);

        await SetWorkerBusy(true, ct);
        await jobSource.ReportJobStartedAsync(job.Id, workerId, ct);

        var project = await storage.GetProjectAsync(job.ProjectId, ct);
        if (project is null)
        {
            await jobSource.ReportJobCompletedAsync(job.Id, false, "Project not found", ct);
            await SetWorkerBusy(false, ct);
            return;
        }

        // Manage disk space before cloning
        await ManageDiskSpaceAsync(ct);

        var localPath = Path.Combine(repoStoragePath, SanitizeName(project.Name));

        var logBuffer = new List<string>();

        async Task FlushLogs()
        {
            if (logBuffer.Count == 0) return;
            await jobSource.SendLogChunkAsync(job.Id, logBuffer.ToArray(), ct);
            logBuffer.Clear();
        }

        async Task Log(string line)
        {
            logger.LogInformation("[Job {Id}] {Line}", job.Id, line);
            logBuffer.Add(line);
            if (logBuffer.Count >= 20)
                await FlushLogs();
        }

        try
        {
            await Log($"=== DotnetFleet Worker | Job {job.Id} ===");
            await Log($"Project: {project.Name} | Branch: {project.Branch}");
            await Log($"Git URL: {project.GitUrl}");

            // Clone or fetch
            await GitHelper.CloneOrFetchAsync(project.GitUrl, project.Branch, localPath,
                async msg => { logBuffer.Add(msg); if (logBuffer.Count >= 20) await FlushLogs(); }, ct);
            await FlushLogs();

            // Update repo cache metadata
            var cacheSize = GitHelper.GetDirectorySize(localPath);
            await storage.UpsertRepoCacheAsync(new RepoCache
            {
                WorkerId = workerId,
                ProjectId = project.Id,
                LocalPath = localPath,
                SizeBytes = cacheSize,
                LastUsedAt = DateTimeOffset.UtcNow,
                LastKnownCommitSha = job.TriggerCommitSha
            }, ct);

            // Run deployer
            await Log("=== Invoking DotnetDeployer ===");

            // Load secrets: globals first, then project-specific (project wins on collision)
            var globalSecrets = await storage.GetSecretsAsync(null, ct);
            var projectSecrets = await storage.GetSecretsAsync(project.Id, ct);

            var envVars = globalSecrets
                .Concat(projectSecrets)
                .GroupBy(s => s.Name)
                .ToDictionary(g => g.Key, g => g.Last().Value);

            var (success, error) = await DeployerRunner.RunAsync(
                localPath,
                onLine: async line =>
                {
                    logBuffer.Add(line);
                    if (logBuffer.Count >= 20)
                        await FlushLogs();
                },
                envVars: envVars,
                ct);

            await FlushLogs();

            if (success)
            {
                await Log("=== Deployment SUCCEEDED ===");
                await jobSource.ReportJobCompletedAsync(job.Id, true, null, ct);
            }
            else
            {
                await Log($"=== Deployment FAILED: {error} ===");
                await jobSource.ReportJobCompletedAsync(job.Id, false, error, ct);
            }
        }
        catch (Exception ex)
        {
            await Log($"[EXCEPTION] {ex.Message}");
            await FlushLogs();
            await jobSource.ReportJobCompletedAsync(job.Id, false, ex.Message, ct);
        }
        finally
        {
            await SetWorkerBusy(false, ct);
        }
    }

    private async Task ManageDiskSpaceAsync(CancellationToken ct)
    {
        var worker = await storage.GetWorkerAsync(workerId, ct);
        if (worker is null || worker.MaxDiskUsageBytes <= 0) return;

        var caches = (await storage.GetRepoCachesAsync(workerId, ct))
            .OrderBy(c => c.LastUsedAt)
            .ToList();

        var totalSize = caches.Sum(c => c.SizeBytes);

        while (totalSize > worker.MaxDiskUsageBytes && caches.Count > 0)
        {
            var oldest = caches[0];
            caches.RemoveAt(0);

            logger.LogInformation("Evicting cached repo {Path} ({SizeMb:F1} MB) to free disk space",
                oldest.LocalPath, oldest.SizeBytes / 1024.0 / 1024);

            try
            {
                if (Directory.Exists(oldest.LocalPath))
                    Directory.Delete(oldest.LocalPath, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete cached repo {Path}", oldest.LocalPath);
            }

            await storage.DeleteRepoCacheAsync(oldest.Id, ct);
            totalSize -= oldest.SizeBytes;
        }
    }

    private async Task SetWorkerBusy(bool busy, CancellationToken ct)
    {
        var worker = await storage.GetWorkerAsync(workerId, ct);
        if (worker is null) return;
        worker.Status = busy ? WorkerStatus.Busy : WorkerStatus.Online;
        await storage.UpdateWorkerAsync(worker, ct);
    }

    private static string SanitizeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidPathChars().Contains(c) ? '_' : c))
              .Replace(' ', '-');
}
