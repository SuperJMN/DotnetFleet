using System.Net.Http.Json;
using System.Net.Http.Headers;
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

    public async Task UploadArtifactAsync(Guid jobId, string relativePath, Stream content, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/queue/jobs/{jobId}/artifacts");
        request.Headers.Add("X-Fleet-Artifact-Path", relativePath.Replace('\\', '/'));
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task PostJobPhaseAsync(Guid jobId, PhaseEvent ev, CancellationToken ct = default)
    {
        var payload = new
        {
            kind = ev.Kind.ToString(),
            name = ev.Name,
            status = ev.Status == PhaseStatus.Unknown ? null : ev.Status.ToString(),
            durationMs = ev.DurationMs,
            message = ev.Message,
            attrs = ev.Attrs.Count == 0 ? null : ev.Attrs
        };
        try
        {
            var resp = await http.PostAsJsonAsync($"/api/queue/jobs/{jobId}/phase", payload, ct);
            if (!resp.IsSuccessStatusCode)
                logger.LogDebug("POST /api/queue/jobs/{JobId}/phase failed with {Status}", jobId, (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Phase telemetry must never break a deployment.
            logger.LogDebug(ex, "Posting phase event for {JobId} failed", jobId);
        }
    }

    public async Task ReportJobCompletedAsync(Guid jobId, bool success, string? errorMessage, CancellationToken ct = default)
    {
        var payload = new { success, errorMessage };
        var resp = await http.PostAsJsonAsync($"/api/queue/jobs/{jobId}/complete", payload, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<JobAction> GetJobActionAsync(Guid jobId, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.GetAsync($"/api/queue/jobs/{jobId}/should-cancel", ct);

            // 404 from an older coordinator (or a deleted job) means the coordinator no
            // longer knows about this job — the worker must release it.
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return JobAction.Abort;

            // Any other non-success keeps the previous "best effort" behaviour: assume
            // the job is still ours and keep working — the next poll will settle it.
            if (!resp.IsSuccessStatusCode)
                return JobAction.Continue;

            var result = await resp.Content.ReadFromJsonAsync<ShouldCancelResponse>(ct);
            if (result is null) return JobAction.Continue;

            // Prefer the new explicit field; fall back to the legacy boolean for
            // backward compatibility with older coordinators that only return
            // { shouldCancel: bool }.
            if (!string.IsNullOrEmpty(result.Action)
                && Enum.TryParse<JobAction>(result.Action, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            return result.ShouldCancel ? JobAction.Cancel : JobAction.Continue;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to check job action for {JobId}", jobId);
            return JobAction.Continue;
        }
    }

    private record ShouldCancelResponse(bool ShouldCancel, string? Action = null);
}
