using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetFleet.WorkerService.Coordinator;

/// <summary>
/// Caches the worker JWT, refreshing via <c>POST /api/workers/login</c> when expiry is near.
/// Thread-safe; concurrent callers share a single in-flight refresh.
/// </summary>
public class WorkerTokenManager
{
    private readonly HttpClient httpClient;
    private readonly WorkerOptions options;
    private readonly ILogger<WorkerTokenManager> logger;

    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private string? cachedToken;
    private DateTimeOffset cachedExpiresAt;

    public WorkerTokenManager(HttpClient httpClient, IOptions<WorkerOptions> options, ILogger<WorkerTokenManager> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        var skew = TimeSpan.FromSeconds(options.TokenRefreshSkewSeconds);
        if (cachedToken is not null && DateTimeOffset.UtcNow + skew < cachedExpiresAt)
            return cachedToken;

        await refreshLock.WaitAsync(ct);
        try
        {
            if (cachedToken is not null && DateTimeOffset.UtcNow + skew < cachedExpiresAt)
                return cachedToken;

            return await LoginAsync(ct);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public void Invalidate()
    {
        cachedToken = null;
        cachedExpiresAt = DateTimeOffset.MinValue;
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        if (options.Id is null || string.IsNullOrEmpty(options.Secret))
            throw new InvalidOperationException("Worker:Id and Worker:Secret must be configured before login.");

        logger.LogInformation("Logging in worker {WorkerId}", options.Id);

        var resp = await httpClient.PostAsJsonAsync("/api/workers/login",
            new { workerId = options.Id, secret = options.Secret }, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Worker login failed ({(int)resp.StatusCode}): {body}");
        }

        var payload = await resp.Content.ReadFromJsonAsync<LoginResponse>(ct)
            ?? throw new InvalidOperationException("Empty login response.");

        cachedToken = payload.Token;
        cachedExpiresAt = ParseExpiry(payload.Token) ?? DateTimeOffset.UtcNow.AddMinutes(60);
        return cachedToken;
    }

    private static DateTimeOffset? ParseExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var json = Convert.FromBase64String(PadBase64(parts[1]));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exp", out var exp))
                return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
        }
        catch
        {
            // ignore – we'll fall back to a default lifetime
        }
        return null;
    }

    private static string PadBase64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    private record LoginResponse(string Token, Guid WorkerId, string Name);
}

/// <summary>
/// Adds <c>Authorization: Bearer &lt;token&gt;</c> to every request, refreshing the token on 401.
/// </summary>
public class WorkerAuthHandler : DelegatingHandler
{
    private readonly WorkerTokenManager tokens;

    public WorkerAuthHandler(WorkerTokenManager tokens) => this.tokens = tokens;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(ct));
        var resp = await base.SendAsync(request, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            tokens.Invalidate();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(ct));
            resp.Dispose();
            resp = await base.SendAsync(request, ct);
        }

        return resp;
    }
}
