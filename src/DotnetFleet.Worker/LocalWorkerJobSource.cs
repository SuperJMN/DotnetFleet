using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;

namespace DotnetFleet.WorkerService;

/// <summary>
/// IWorkerJobSource implementation that accesses the coordinator storage directly
/// (used when the worker is embedded in the coordinator process).
/// </summary>
public class LocalWorkerJobSource : IWorkerJobSource
{
    private readonly IFleetStorage storage;

    public LocalWorkerJobSource(IFleetStorage storage) => this.storage = storage;

    public Task<DeploymentJob?> GetNextJobAsync(Guid workerId, CancellationToken ct = default) =>
        storage.DequeueNextJobAsync(ct);

    public async Task ReportJobStartedAsync(Guid jobId, Guid workerId, CancellationToken ct = default)
    {
        var job = await storage.GetJobAsync(jobId, ct);
        if (job is null) return;
        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        job.WorkerId = workerId;
        await storage.UpdateJobAsync(job, ct);
    }

    public async Task SendLogChunkAsync(Guid jobId, IEnumerable<string> lines, CancellationToken ct = default)
    {
        var entries = lines.Select(line => new LogEntry
        {
            JobId = jobId,
            Line = line,
            Timestamp = DateTimeOffset.UtcNow
        });
        await storage.AddLogEntriesAsync(entries, ct);
    }

    public async Task ReportJobCompletedAsync(Guid jobId, bool success, string? errorMessage, CancellationToken ct = default)
    {
        var job = await storage.GetJobAsync(jobId, ct);
        if (job is null) return;
        job.Status = success ? JobStatus.Succeeded : JobStatus.Failed;
        job.FinishedAt = DateTimeOffset.UtcNow;
        job.ErrorMessage = errorMessage;
        await storage.UpdateJobAsync(job, ct);
    }
}
