using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using DotnetFleet.WorkerService.Coordinator;
using DotnetFleet.WorkerService.Execution;
using DotnetFleet.WorkerService.Git;
using DotnetFleet.WorkerService.RepoStorage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetFleet.WorkerService;

/// <summary>
/// Standalone worker host loop:
///   - heartbeats the coordinator
///   - polls for the next assigned job
///   - clones/fetches the repo, runs DotnetDeployer, streams logs and final status
///   - reports cached repos and evicts old ones to respect the disk budget
/// All coordinator interaction goes through HTTP — there is no shared DB.
/// </summary>
public class RemoteWorkerBackgroundService : BackgroundService
{
    private readonly IWorkerJobSource jobSource;
    private readonly IWorkerCoordinatorClient coordinator;
    private readonly WorkerOptions options;
    private readonly ILogger<RemoteWorkerBackgroundService> logger;

    private Guid workerId;
    private string repoStoragePath = "fleet-repos";

    public RemoteWorkerBackgroundService(
        IWorkerJobSource jobSource,
        IWorkerCoordinatorClient coordinator,
        IOptions<WorkerOptions> options,
        ILogger<RemoteWorkerBackgroundService> logger)
    {
        this.jobSource = jobSource;
        this.coordinator = coordinator;
        this.options = options.Value;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (this.options.Id is not { } id)
        {
            logger.LogError("Worker has no Id assigned. Bootstrap should have populated it.");
            return;
        }
        workerId = id;
        repoStoragePath = this.options.RepoStoragePath;
        Directory.CreateDirectory(repoStoragePath);
        RepoStorageIsolator.EnsureBarrierFiles(repoStoragePath);

        logger.LogInformation(
            "Worker {Id} started. Coordinator: {Url}. Repos: {Path} (CPM-isolated)",
            workerId, this.options.CoordinatorBaseUrl, repoStoragePath);

        // Initial registration of self with the coordinator (status=Online).
        try { await coordinator.UpdateStatusAsync(workerId, WorkerStatus.Online, stoppingToken); }
        catch (Exception ex) { logger.LogWarning(ex, "Initial status announce failed."); }

        using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(this.options.HeartbeatIntervalSeconds));
        using var pollTimer = new PeriodicTimer(TimeSpan.FromSeconds(this.options.PollIntervalSeconds));

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
                await coordinator.SendHeartbeatAsync(workerId, ct);
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

        await using var logBuffer = new LogChunkBuffer(
            send: (chunk, token) => jobSource.SendLogChunkAsync(job.Id, chunk, token),
            ct: ct);

        async Task Log(string line)
        {
            logger.LogInformation("[Job {Id}] {Line}", job.Id, line);
            await logBuffer.AppendAsync(line);
        }

        try
        {
            await SetWorkerBusy(true, ct);
            await jobSource.ReportJobStartedAsync(job.Id, workerId, ct);

            var project = await coordinator.GetProjectAsync(job.ProjectId, ct);
            if (project is null)
            {
                await jobSource.ReportJobCompletedAsync(job.Id, false, "Project not found", ct);
                return;
            }

            await ManageDiskSpaceAsync(ct);

            var localPath = Path.Combine(repoStoragePath, SanitizeName(project.Name));

            using var cancelCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cancelCts.Token);
            var jobCt = linkedCts.Token;

            _ = PollForCancellationAsync(job.Id, cancelCts, ct);

            await Log($"=== DotnetFleet Worker | Job {job.Id} ===");
            await Log($"Project: {project.Name} | Branch: {project.Branch}");
            await Log($"Git URL: {project.GitUrl}");

            await GitHelper.CloneOrFetchAsync(project.GitUrl, project.Branch, localPath,
                msg => logBuffer.AppendAsync(msg), jobCt, project.GitToken);
            await logBuffer.FlushAsync();

            var cacheSize = GitHelper.GetDirectorySize(localPath);
            try
            {
                await coordinator.UpsertRepoCacheAsync(workerId, new RepoCache
                {
                    WorkerId = workerId,
                    ProjectId = project.Id,
                    LocalPath = localPath,
                    SizeBytes = cacheSize,
                    LastUsedAt = DateTimeOffset.UtcNow,
                    LastKnownCommitSha = job.TriggerCommitSha
                }, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to upsert repo cache metadata.");
            }

            await Log("=== Invoking DotnetDeployer ===");

            var globalSecrets = await coordinator.GetGlobalSecretsAsync(ct);
            var projectSecrets = await coordinator.GetProjectSecretsAsync(project.Id, ct);

            var envVars = globalSecrets
                .Concat(projectSecrets)
                .GroupBy(s => s.Name)
                .ToDictionary(g => g.Key, g => g.Last().Value);

            var (success, error) = await DeployerRunner.RunAsync(
                localPath,
                onLine: line => logBuffer.AppendAsync(line),
                envVars: envVars,
                jobCt);

            await logBuffer.FlushAsync();

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Host shutting down — let it propagate
        }
        catch (OperationCanceledException)
        {
            // User-initiated cancellation
            try
            {
                await Log("=== Deployment CANCELLED by user ===");
                await logBuffer.FlushAsync();
            }
            catch { /* best effort */ }
            await ReportJobFailedBestEffort(job.Id, "Cancelled by user", ct);
        }
        catch (Exception ex)
        {
            try
            {
                await Log($"[EXCEPTION] {ex.Message}");
                await logBuffer.FlushAsync();
            }
            catch { /* best effort */ }
            await ReportJobFailedBestEffort(job.Id, ex.Message, ct);
        }
        finally
        {
            await SetWorkerBusy(false, ct);
        }
    }

    private async Task ReportJobFailedBestEffort(Guid jobId, string error, CancellationToken ct)
    {
        try
        {
            await jobSource.ReportJobCompletedAsync(jobId, false, error, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to report job {JobId} as failed (error: {Error})", jobId, error);
        }
    }

    private async Task PollForCancellationAsync(Guid jobId, CancellationTokenSource cancelCts, CancellationToken hostCt)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(hostCt))
            {
                if (cancelCts.IsCancellationRequested) break;
                if (await jobSource.IsJobCancelledAsync(jobId, hostCt))
                {
                    logger.LogInformation("Cancellation requested for job {JobId}", jobId);
                    await cancelCts.CancelAsync();
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* host shutting down or job finished */ }
        catch (Exception ex) { logger.LogWarning(ex, "Cancellation poll error for job {JobId}", jobId); }
    }

    private async Task ManageDiskSpaceAsync(CancellationToken ct)
    {
        if (!options.MaxDiskUsageBytes.HasValue || options.MaxDiskUsageBytes.Value <= 0) return;
        var budget = options.MaxDiskUsageBytes.Value;

        IReadOnlyList<RepoCache> caches;
        try
        {
            caches = await coordinator.GetRepoCachesAsync(workerId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch repo caches; skipping disk-space management.");
            return;
        }

        var ordered = caches.OrderBy(c => c.LastUsedAt).ToList();
        var totalSize = ordered.Sum(c => c.SizeBytes);

        while (totalSize > budget && ordered.Count > 0)
        {
            var oldest = ordered[0];
            ordered.RemoveAt(0);

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

            try { await coordinator.DeleteRepoCacheAsync(workerId, oldest.Id, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to delete cache record {Id}", oldest.Id); }

            totalSize -= oldest.SizeBytes;
        }
    }

    private async Task SetWorkerBusy(bool busy, CancellationToken ct)
    {
        try
        {
            await coordinator.UpdateStatusAsync(
                workerId,
                busy ? WorkerStatus.Busy : WorkerStatus.Online,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update worker status to {Busy}", busy);
        }
    }

    private static string SanitizeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidPathChars().Contains(c) ? '_' : c))
              .Replace(' ', '-');
}
