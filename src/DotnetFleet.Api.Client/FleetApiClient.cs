using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetFleet.Core.Domain;

namespace DotnetFleet.Api.Client;

public class FleetApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient http;
    // Long-lived streams (SSE) need an HttpClient with no Timeout, since HttpClient.Timeout
    // covers the entire response — including body reads — even with ResponseHeadersRead.
    private readonly HttpClient streamingHttp;
    private readonly BehaviorSubject<Uri?> _baseAddressSubject;
    private string? token;

    public FleetApiClient(HttpClient http, HttpClient streamingHttp)
        : this(http, streamingHttp, Observable.Empty<System.Reactive.Unit>())
    {
    }

    public FleetApiClient(HttpClient http, HttpClient streamingHttp, IObservable<System.Reactive.Unit> unauthorizedSignal)
    {
        this.http = http;
        this.streamingHttp = streamingHttp;
        _baseAddressSubject = new BehaviorSubject<Uri?>(http.BaseAddress);
        Unauthorized = unauthorizedSignal;
    }

    /// <summary>
    /// Emits whenever the server rejects a request with 401 Unauthorized — meaning the
    /// stored token is no longer valid (expired, signed with a different secret, etc.).
    /// Subscribers should clear any persisted token and return to the login screen.
    /// </summary>
    public IObservable<System.Reactive.Unit> Unauthorized { get; }

    public Uri? BaseAddress => http.BaseAddress;

    /// <summary>
    /// Emits whenever <see cref="BaseAddress"/> changes (and once on subscribe with the current value).
    /// </summary>
    public IObservable<Uri?> BaseAddressChanges => _baseAddressSubject.AsObservable();

    public void SetBaseAddress(string baseUrl)
    {
        var uri = new Uri(baseUrl.TrimEnd('/') + "/");
        http.BaseAddress = uri;
        streamingHttp.BaseAddress = uri;
        _baseAddressSubject.OnNext(uri);
    }

    public void SetToken(string jwt)
    {
        token = jwt;
        var auth = new AuthenticationHeaderValue("Bearer", jwt);
        http.DefaultRequestHeaders.Authorization = auth;
        streamingHttp.DefaultRequestHeaders.Authorization = auth;
    }

    public void ClearToken()
    {
        token = null;
        http.DefaultRequestHeaders.Authorization = null;
        streamingHttp.DefaultRequestHeaders.Authorization = null;
    }

    public bool IsAuthenticated => token is not null;

    // ── Auth ─────────────────────────────────────────────────────────────────

    public async Task<LoginResponse?> LoginAsync(string username, string password)
    {
        var response = await http.PostAsJsonAsync("/api/auth/login",
            new { username, password }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
    }

    // ── Projects ──────────────────────────────────────────────────────────────

    public async Task<List<Project>> GetProjectsAsync() =>
        await http.GetFromJsonAsync<List<Project>>("/api/projects", JsonOptions) ?? [];

    public async Task<Project> CreateProjectAsync(string name, string gitUrl, string branch, int pollingIntervalMinutes = 0, string? gitToken = null)
    {
        var response = await http.PostAsJsonAsync("/api/projects",
            new { name, gitUrl, branch, pollingIntervalMinutes, gitToken }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Project>(JsonOptions))!;
    }

    public async Task UpdateProjectAsync(Guid id, string? name = null, string? gitUrl = null, string? branch = null, int? pollingIntervalMinutes = null, string? gitToken = null)
    {
        var response = await http.PutAsJsonAsync($"/api/projects/{id}",
            new { name, gitUrl, branch, pollingIntervalMinutes, gitToken }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteProjectAsync(Guid id)
    {
        var response = await http.DeleteAsync($"/api/projects/{id}");
        response.EnsureSuccessStatusCode();
    }

    public async Task<DeploymentJob> EnqueueDeployAsync(Guid projectId)
    {
        var response = await http.PostAsync($"/api/projects/{projectId}/deploy", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeploymentJob>(JsonOptions))!;
    }

    public async Task<List<DeploymentJob>> GetProjectJobsAsync(Guid projectId) =>
        await http.GetFromJsonAsync<List<DeploymentJob>>($"/api/projects/{projectId}/jobs", JsonOptions) ?? [];

    // ── Jobs ──────────────────────────────────────────────────────────────────

    public async Task<List<DeploymentJob>> GetAllJobsAsync() =>
        await http.GetFromJsonAsync<List<DeploymentJob>>("/api/jobs", JsonOptions) ?? [];

    public async Task<DeploymentJob?> GetJobAsync(Guid id) =>
        await http.GetFromJsonAsync<DeploymentJob>($"/api/jobs/{id}", JsonOptions);

    public async Task CancelJobAsync(Guid jobId)
    {
        var response = await http.PostAsync($"/api/jobs/{jobId}/cancel", null);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Returns an async enumerable of log lines streamed via SSE.
    /// Completes when the job finishes or the cancellation token is triggered.
    /// </summary>
    public async IAsyncEnumerable<string> StreamJobLogsAsync(
        Guid jobId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Use the dedicated streamingHttp (no Timeout) so the SSE connection stays open.
        using var response = await streamingHttp.GetAsync($"/api/jobs/{jobId}/logs",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            // SSE: skip comment/keep-alive lines (start with ':') and blank separators
            if (line.Length == 0 || line[0] == ':') continue;

            if (line.StartsWith("data: "))
                yield return line[6..];
        }
    }

    /// <summary>
    /// Sends a request bypassing <c>HttpClient.Timeout</c> so the response body can be streamed indefinitely.
    /// </summary>
    private async Task<HttpResponseMessage> SendStreamingAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Linking with a CTS that we never cancel effectively disables the per-call timeout.
        var noTimeoutCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, noTimeoutCts.Token);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
    }

    // ── Workers ───────────────────────────────────────────────────────────────

    public async Task<List<WorkerInfo>> GetWorkersAsync() =>
        await http.GetFromJsonAsync<List<WorkerInfo>>("/api/workers", JsonOptions) ?? [];

    public async Task UpdateWorkerConfigAsync(Guid workerId, double? maxDiskUsageGb = null, string? repoStoragePath = null)
    {
        var response = await http.PutAsJsonAsync($"/api/workers/{workerId}/config",
            new { maxDiskUsageGb, repoStoragePath }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public record LoginResponse(string Token, string Username, string Role);

    public record WorkerInfo(
        Guid Id,
        string Name,
        WorkerStatus Status,
        bool IsEmbedded,
        DateTimeOffset? LastSeenAt,
        double MaxDiskUsageGb,
        string? RepoStoragePath);

    // ── Secrets ───────────────────────────────────────────────────────────────

    public async Task<List<Secret>> GetSecretsAsync() =>
        await http.GetFromJsonAsync<List<Secret>>("/api/secrets", JsonOptions) ?? [];

    public async Task<Secret> CreateSecretAsync(string name, string value)
    {
        var response = await http.PostAsJsonAsync("/api/secrets", new { name, value }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Secret>(JsonOptions))!;
    }

    public async Task UpdateSecretAsync(Guid id, string name, string value)
    {
        var response = await http.PutAsJsonAsync($"/api/secrets/{id}", new { name, value }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteSecretAsync(Guid id)
    {
        var response = await http.DeleteAsync($"/api/secrets/{id}");
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<Secret>> GetProjectSecretsAsync(Guid projectId) =>
        await http.GetFromJsonAsync<List<Secret>>($"/api/projects/{projectId}/secrets", JsonOptions) ?? [];

    public async Task<Secret> CreateProjectSecretAsync(Guid projectId, string name, string value)
    {
        var response = await http.PostAsJsonAsync($"/api/projects/{projectId}/secrets",
            new { name, value }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Secret>(JsonOptions))!;
    }

    public async Task UpdateProjectSecretAsync(Guid projectId, Guid secretId, string name, string value)
    {
        var response = await http.PutAsJsonAsync($"/api/projects/{projectId}/secrets/{secretId}",
            new { name, value }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteProjectSecretAsync(Guid projectId, Guid secretId)
    {
        var response = await http.DeleteAsync($"/api/projects/{projectId}/secrets/{secretId}");
        response.EnsureSuccessStatusCode();
    }
}
