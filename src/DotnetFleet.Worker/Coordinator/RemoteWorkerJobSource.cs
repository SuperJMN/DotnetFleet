using System.Net.Http.Json;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DotnetFleet.WorkerService.Coordinator;

/// <summary>
/// HTTP implementation of <see cref="IWorkerJobSource"/> that talks to the coordinator
/// via the <c>/api/queue/*</c> endpoints. JWT bearer is added by the auth handler on the
/// shared <see cref="HttpClient"/>.
/// </summary>
public class RemoteWorkerJobSource : IWorkerJobSource
{
    private readonly HttpClient http;
    private readonly ILogger<RemoteWorkerJobSource> logger;

    public RemoteWorkerJobSource(HttpClient http, ILogger<RemoteWorkerJobSource> logger)
    {
        this.http = http;
        this.logger = logger;
    }

    public async Task<DeploymentJob?> GetNextJobAsync(Guid workerId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync("/api/queue/next", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("GET /api/queue/next failed with {Status}", (int)resp.StatusCode);
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<DeploymentJob>(ct);
    }

    public async Task ReportJobStartedAsync(Guid jobId, Guid workerId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"/api/queue/jobs/{jobId}/start", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task SendLogChunkAsync(Guid jobId, IEnumerable<string> lines, CancellationToken ct = default)
    {
        var payload = new { lines = lines.ToArray() };
        var resp = await http.PostAsJsonAsync($"/api/queue/jobs/{jobId}/logs", payload, ct);
        if (!resp.IsSuccessStatusCode)
            logger.LogWarning("POST /api/queue/jobs/{JobId}/logs failed with {Status}", jobId, (int)resp.StatusCode);
    }

    public async Task ReportJobCompletedAsync(Guid jobId, bool success, string? errorMessage, CancellationToken ct = default)
    {
        var payload = new { success, errorMessage };
        var resp = await http.PostAsJsonAsync($"/api/queue/jobs/{jobId}/complete", payload, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<bool> IsJobCancelledAsync(Guid jobId, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.GetAsync($"/api/queue/jobs/{jobId}/should-cancel", ct);
            if (!resp.IsSuccessStatusCode) return false;
            var result = await resp.Content.ReadFromJsonAsync<ShouldCancelResponse>(ct);
            return result?.ShouldCancel ?? false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to check cancellation for job {JobId}", jobId);
            return false;
        }
    }

    private record ShouldCancelResponse(bool ShouldCancel);
}
