using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// Periodically checks each project's branch for new commits.
/// When a new commit SHA is detected, a deployment job is automatically enqueued.
/// </summary>
public class PollingBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<PollingBackgroundService> logger;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    public PollingBackgroundService(IServiceScopeFactory scopeFactory, ILogger<PollingBackgroundService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllProjectsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in polling loop");
            }

            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    protected virtual async Task PollAllProjectsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFleetStorage>();

        var projects = await storage.GetProjectsAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var project in projects)
        {
            if (project.PollingIntervalMinutes <= 0) continue;

            var nextPoll = project.LastPolledAt?.AddMinutes(project.PollingIntervalMinutes) ?? DateTimeOffset.MinValue;
            if (now < nextPoll) continue;

            await PollProjectAsync(storage, project, ct);
        }
    }

    private async Task PollProjectAsync(IFleetStorage storage, Project project, CancellationToken ct)
    {
        logger.LogDebug("Polling project {Name} ({Branch})", project.Name, project.Branch);

        var latestSha = await GetLatestCommitShaAsync(project.GitUrl, project.Branch, ct);

        project.LastPolledAt = DateTimeOffset.UtcNow;

        if (latestSha is null)
        {
            logger.LogWarning("Could not resolve SHA for {Name}/{Branch}", project.Name, project.Branch);
            await storage.UpdateProjectAsync(project);
            return;
        }

        if (latestSha == project.LastPolledCommitSha)
        {
            await storage.UpdateProjectAsync(project);
            return;
        }

        logger.LogInformation("New commit on {Name}/{Branch}: {Sha} → enqueuing deploy", project.Name, project.Branch, latestSha);

        var job = new DeploymentJob
        {
            ProjectId = project.Id,
            TriggerCommitSha = latestSha,
            IsAutoTriggered = true
        };

        project.LastPolledCommitSha = latestSha;

        await storage.AddJobAsync(job, ct);
        await storage.UpdateProjectAsync(project, ct);
    }

    private async Task<string?> GetLatestCommitShaAsync(string gitUrl, string branch, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"ls-remote --heads {gitUrl} refs/heads/{branch}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            // Output format: "<sha>\trefs/heads/<branch>"
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return line?.Split('\t').FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "git ls-remote failed for {Url}", gitUrl);
            return null;
        }
    }
}
