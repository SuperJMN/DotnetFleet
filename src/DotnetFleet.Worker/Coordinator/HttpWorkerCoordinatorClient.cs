using System.Net.Http.Json;
using DotnetFleet.Core.Domain;
using Microsoft.Extensions.Logging;

namespace DotnetFleet.WorkerService.Coordinator;

public class HttpWorkerCoordinatorClient : IWorkerCoordinatorClient
{
    private readonly HttpClient http;
    private readonly ILogger<HttpWorkerCoordinatorClient> logger;

    public HttpWorkerCoordinatorClient(HttpClient http, ILogger<HttpWorkerCoordinatorClient> logger)
    {
        this.http = http;
        this.logger = logger;
    }

    public async Task<Worker?> GetSelfAsync(CancellationToken ct = default)
    {
        var resp = await http.GetAsync("/api/workers/me", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Worker>(ct);
    }

    public async Task SendHeartbeatAsync(Guid workerId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"/api/workers/{workerId}/heartbeat", content: null, ct);
        if (!resp.IsSuccessStatusCode)
            logger.LogWarning("Heartbeat returned {Status}", (int)resp.StatusCode);
    }

    public async Task UpdateStatusAsync(Guid workerId, WorkerStatus status, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync($"/api/workers/{workerId}/status", new { status }, ct);
        if (!resp.IsSuccessStatusCode)
            logger.LogWarning("UpdateStatus returned {Status}", (int)resp.StatusCode);
    }

    public async Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/api/projects/{projectId}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Project>(ct);
    }

    public async Task<IReadOnlyList<Secret>> GetGlobalSecretsAsync(CancellationToken ct = default)
    {
        var resp = await http.GetAsync("/api/secrets", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<Secret>>(ct) ?? new();
    }

    public async Task<IReadOnlyList<Secret>> GetProjectSecretsAsync(Guid projectId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/api/projects/{projectId}/secrets", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<Secret>>(ct) ?? new();
    }

    public async Task<IReadOnlyList<RepoCache>> GetRepoCachesAsync(Guid workerId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/api/workers/{workerId}/repo-caches", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<RepoCache>>(ct) ?? new();
    }

    public async Task UpsertRepoCacheAsync(Guid workerId, RepoCache cache, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync($"/api/workers/{workerId}/repo-caches", cache, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteRepoCacheAsync(Guid workerId, Guid cacheId, CancellationToken ct = default)
    {
        var resp = await http.DeleteAsync($"/api/workers/{workerId}/repo-caches/{cacheId}", ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            resp.EnsureSuccessStatusCode();
    }
}
