using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DotnetFleet.WorkerService.Bootstrap;

/// <summary>
/// Resolves the worker identity (Id + Secret) before the host starts.
/// Order:
///   1. Use values already in <see cref="WorkerOptions"/> (env, appsettings).
///   2. Load from <see cref="WorkerOptions.CredentialsFile"/> if present.
///   3. Self-register against the coordinator using <see cref="WorkerOptions.RegistrationToken"/>
///      and persist the resulting credentials to disk.
/// </summary>
public static class WorkerBootstrap
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task EnsureCredentialsAsync(WorkerOptions options, ILogger logger, CancellationToken ct = default)
    {
        if (options.Id is { } id && !string.IsNullOrEmpty(options.Secret))
        {
            logger.LogInformation("Worker credentials provided via configuration (id={Id})", id);
            return;
        }

        var credsPath = options.CredentialsFile;
        if (File.Exists(credsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(credsPath, ct);
                var stored = JsonSerializer.Deserialize<StoredCredentials>(json, JsonOptions);
                if (stored is not null && stored.WorkerId != Guid.Empty && !string.IsNullOrEmpty(stored.Secret))
                {
                    options.Id = stored.WorkerId;
                    options.Secret = stored.Secret;
                    logger.LogInformation("Loaded worker credentials from {Path} (id={Id})", credsPath, stored.WorkerId);
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read credentials file {Path}; will attempt re-registration.", credsPath);
            }
        }

        if (string.IsNullOrEmpty(options.RegistrationToken))
        {
            throw new InvalidOperationException(
                $"No worker credentials found. Either configure Worker:Id + Worker:Secret, " +
                $"provide a {credsPath} file, or set Worker:RegistrationToken to allow self-registration.");
        }

        logger.LogInformation("Registering this worker against {Url}", options.CoordinatorBaseUrl);

        using var http = new HttpClient { BaseAddress = new Uri(options.CoordinatorBaseUrl) };
        http.DefaultRequestHeaders.Add("X-Registration-Token", options.RegistrationToken);

        var name = options.Name ?? Environment.MachineName;
        var body = new
        {
            name,
            isEmbedded = false,
            maxDiskUsageGb = options.MaxDiskUsageBytes.HasValue
                ? options.MaxDiskUsageBytes.Value / (1024.0 * 1024 * 1024)
                : (double?)null,
            repoStoragePath = options.RepoStoragePath
        };

        var resp = await http.PostAsJsonAsync("/api/workers/register", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Worker registration failed ({(int)resp.StatusCode}): {err}");
        }

        var registered = await resp.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty register response.");

        options.Id = registered.WorkerId;
        options.Secret = registered.Secret;

        var toStore = new StoredCredentials(registered.WorkerId, registered.Secret);
        var dir = Path.GetDirectoryName(Path.GetFullPath(credsPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(credsPath, JsonSerializer.Serialize(toStore, JsonOptions), ct);
        logger.LogInformation("Persisted new worker credentials to {Path} (id={Id})", credsPath, registered.WorkerId);
    }

    private record StoredCredentials(Guid WorkerId, string Secret);
    private record RegisterResponse(Guid WorkerId, string Secret);
}
