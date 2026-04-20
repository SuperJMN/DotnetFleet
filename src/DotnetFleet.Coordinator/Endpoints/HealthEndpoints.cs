using System.Reflection;

namespace DotnetFleet.Coordinator.Endpoints;

public static class HealthEndpoints
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private static readonly string Version =
        typeof(HealthEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HealthEndpoints).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new
            {
                status = "ok",
                service = "DotnetFleet.Coordinator",
                version = Version,
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
                serverTime = DateTimeOffset.UtcNow
            }))
            .AllowAnonymous()
            .WithName("Health");
    }
}
