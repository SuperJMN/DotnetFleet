using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetFleet.Core.Domain;

namespace DotnetFleet.Tool;

internal sealed class FleetAdminClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<WorkerUnregisterResult> UnregisterWorkerByName(
        string workerName,
        string adminUser,
        string adminPassword,
        CancellationToken ct = default)
    {
        var loggedIn = await Login(adminUser, adminPassword, ct);
        if (!loggedIn)
            return WorkerUnregisterResult.AuthenticationFailed();

        using var workersResponse = await http.GetAsync("/api/workers", ct);
        if (IsForbidden(workersResponse.StatusCode))
            return WorkerUnregisterResult.Forbidden();

        workersResponse.EnsureSuccessStatusCode();
        var workers = await workersResponse.Content.ReadFromJsonAsync<List<WorkerInfo>>(JsonOptions, ct) ?? [];
        var matches = workers
            .Where(w => string.Equals(w.Name, workerName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return WorkerUnregisterResult.NotFound();

        if (matches.Count > 1)
            return WorkerUnregisterResult.Ambiguous(matches.Count);

        var worker = matches[0];
        using var deleteResponse = await http.DeleteAsync($"/api/workers/{worker.Id}", ct);
        if (deleteResponse.StatusCode == HttpStatusCode.NotFound)
            return WorkerUnregisterResult.DeleteEndpointUnavailable(worker.Id, worker.Name);

        if (IsForbidden(deleteResponse.StatusCode))
            return WorkerUnregisterResult.Forbidden();

        deleteResponse.EnsureSuccessStatusCode();
        var deleted = await deleteResponse.Content.ReadFromJsonAsync<DeleteWorkerResponse>(JsonOptions, ct);

        return WorkerUnregisterResult.Deleted(
            deleted?.Id ?? worker.Id,
            deleted?.Name ?? worker.Name,
            deleted?.FailedJobs ?? 0);
    }

    private async Task<bool> Login(string username, string password, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(username, password),
            JsonOptions,
            ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return false;

        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, ct);
        if (string.IsNullOrWhiteSpace(login?.Token))
            return false;

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return true;
    }

    private static bool IsForbidden(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private sealed record LoginRequest(string Username, string Password);
    private sealed record LoginResponse(string Token, string Username, string Role);
    private sealed record WorkerInfo(Guid Id, string Name, WorkerStatus Status);
    private sealed record DeleteWorkerResponse(Guid Id, string Name, int FailedJobs);
}

internal enum WorkerUnregisterStatus
{
    Deleted,
    NotFound,
    AuthenticationFailed,
    Forbidden,
    Ambiguous,
    DeleteEndpointUnavailable
}

internal sealed record WorkerUnregisterResult(
    WorkerUnregisterStatus Status,
    Guid? WorkerId = null,
    string? WorkerName = null,
    int FailedJobs = 0,
    int Matches = 0)
{
    public static WorkerUnregisterResult Deleted(Guid workerId, string workerName, int failedJobs) =>
        new(WorkerUnregisterStatus.Deleted, workerId, workerName, failedJobs);

    public static WorkerUnregisterResult NotFound() =>
        new(WorkerUnregisterStatus.NotFound);

    public static WorkerUnregisterResult AuthenticationFailed() =>
        new(WorkerUnregisterStatus.AuthenticationFailed);

    public static WorkerUnregisterResult Forbidden() =>
        new(WorkerUnregisterStatus.Forbidden);

    public static WorkerUnregisterResult Ambiguous(int matches) =>
        new(WorkerUnregisterStatus.Ambiguous, Matches: matches);

    public static WorkerUnregisterResult DeleteEndpointUnavailable(Guid workerId, string workerName) =>
        new(WorkerUnregisterStatus.DeleteEndpointUnavailable, workerId, workerName);
}
