using System.Collections.Concurrent;
using DotnetFleet.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DotnetFleet.Coordinator.Endpoints;

/// <summary>
/// Endpoint filter that refreshes the worker's <c>LastSeenAt</c> on every
/// authenticated worker request (log uploads, cancel polls, status reports,
/// …), not just heartbeats.
///
/// This makes liveness detection robust against the heartbeat path being
/// starved or temporarily blocked while the worker is otherwise busy
/// (e.g. piping thousands of stdout lines from a long build): as long as
/// any worker request reaches the coordinator, the stale-job reaper will
/// not declare the worker dead.
///
/// Updates are throttled per worker (one DB write at most every
/// <see cref="MinInterval"/>) so that high-rate endpoints like log uploads
/// don't translate into one UPDATE per request.
/// </summary>
public sealed class WorkerLivenessFilter : IEndpointFilter
{
    /// <summary>
    /// Maximum frequency at which we'll write <c>LastSeenAt</c> for a single
    /// worker. Must stay well below <c>StaleJobReaperService.StaleThreshold</c>
    /// (90 s) to never let a worker be falsely reaped.
    /// </summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> LastTouched = new();

    private readonly IFleetStorage storage;
    private readonly ILogger<WorkerLivenessFilter> logger;

    public WorkerLivenessFilter(IFleetStorage storage, ILogger<WorkerLivenessFilter> logger)
    {
        this.storage = storage;
        this.logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var workerId = TryGetWorkerId(context.HttpContext);
        if (workerId is { } id)
        {
            var now = DateTimeOffset.UtcNow;
            var last = LastTouched.GetValueOrDefault(id);
            if (now - last >= MinInterval)
            {
                LastTouched[id] = now;
                _ = TouchSafelyAsync(id, context.HttpContext.RequestAborted);
            }
        }

        return await next(context);
    }

    private async Task TouchSafelyAsync(Guid workerId, CancellationToken ct)
    {
        try
        {
            await storage.TouchWorkerAsync(workerId, ct);
        }
        catch (OperationCanceledException) { /* request aborted */ }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to refresh LastSeenAt for worker {WorkerId}", workerId);
        }
    }

    private static Guid? TryGetWorkerId(HttpContext ctx)
    {
        var raw = ctx.User.FindFirst("worker_id")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
