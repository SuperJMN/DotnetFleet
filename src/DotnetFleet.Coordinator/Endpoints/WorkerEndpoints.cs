using static BCrypt.Net.BCrypt;
using DotnetFleet.Coordinator.Auth;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DotnetFleet.Coordinator.Endpoints;

public static class WorkerEndpoints
{
    public static void MapWorkerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workers");

        // Admin-only management
        group.MapGet("/", GetAll).RequireAuthorization("Admin");
        group.MapPut("/{id:guid}/config", UpdateConfig).RequireAuthorization("Admin");

        // Bootstrap: anonymous if a valid X-Registration-Token header is supplied,
        // otherwise requires Admin. This lets new workers self-register without
        // an admin pre-flight when an out-of-band token is shared.
        group.MapPost("/register", Register).AllowAnonymous();

        // Worker bootstrap: exchange (workerId, secret) for a JWT carrying Role=Worker.
        group.MapPost("/login", Login).AllowAnonymous();

        // Worker self-service (must present a Worker JWT whose worker_id matches the route id).
        group.MapPost("/{id:guid}/heartbeat", Heartbeat).RequireAuthorization("Worker");
        group.MapPost("/{id:guid}/status", UpdateStatus).RequireAuthorization("Worker");
        group.MapGet("/me", GetSelf).RequireAuthorization("Worker");

        // Repo cache metadata (worker tells coordinator what it has cached, for visibility/eviction).
        group.MapGet("/{id:guid}/repo-caches", GetRepoCaches).RequireAuthorization("Worker");
        group.MapPost("/{id:guid}/repo-caches", UpsertRepoCache).RequireAuthorization("Worker");
        group.MapDelete("/{id:guid}/repo-caches/{cacheId:guid}", DeleteRepoCache).RequireAuthorization("Worker");
    }

    private static async Task<IResult> GetAll(IFleetStorage storage)
    {
        var workers = await storage.GetWorkersAsync();
        return Results.Ok(workers.Select(w => new
        {
            w.Id, w.Name, w.Status, w.IsEmbedded, w.LastSeenAt,
            maxDiskUsageGb = w.MaxDiskUsageBytes / (1024.0 * 1024 * 1024),
            w.RepoStoragePath
        }));
    }

    private static async Task<IResult> Register(
        [FromBody] RegisterWorkerRequest req,
        HttpContext httpContext,
        IFleetStorage storage,
        IConfiguration config)
    {
        var isAdmin = httpContext.User.IsInRole("Admin");
        if (!isAdmin)
        {
            var expected = config["Workers:RegistrationToken"];
            var provided = httpContext.Request.Headers["X-Registration-Token"].ToString();
            if (string.IsNullOrEmpty(expected) || provided != expected)
                return Results.Unauthorized();
        }

        var rawSecret = Guid.NewGuid().ToString("N");
        var worker = new Worker
        {
            Name = req.Name,
            SecretHash = HashPassword(rawSecret),
            IsEmbedded = req.IsEmbedded,
            MaxDiskUsageBytes = req.MaxDiskUsageGb.HasValue
                ? (long)(req.MaxDiskUsageGb.Value * 1024 * 1024 * 1024)
                : 10L * 1024 * 1024 * 1024,
            RepoStoragePath = req.RepoStoragePath
        };

        await storage.AddWorkerAsync(worker);
        return Results.Created($"/api/workers/{worker.Id}",
            new { workerId = worker.Id, secret = rawSecret });
    }

    private static async Task<IResult> Login(
        [FromBody] WorkerLoginRequest req,
        IFleetStorage storage,
        JwtService jwt)
    {
        var worker = await storage.GetWorkerAsync(req.WorkerId);
        if (worker is null || !Verify(req.Secret, worker.SecretHash))
            return Results.Unauthorized();

        var token = jwt.GenerateWorkerToken(worker);
        return Results.Ok(new { token, workerId = worker.Id, name = worker.Name });
    }

    private static async Task<IResult> Heartbeat(
        Guid id,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        var claimedId = httpContext.User.FindFirst("worker_id")?.Value;
        if (!Guid.TryParse(claimedId, out var tokenWorkerId) || tokenWorkerId != id)
            return Results.Forbid();

        var worker = await storage.GetWorkerAsync(id);
        if (worker is null) return Results.NotFound();

        worker.LastSeenAt = DateTimeOffset.UtcNow;
        if (worker.Status == WorkerStatus.Offline)
            worker.Status = WorkerStatus.Online;
        await storage.UpdateWorkerAsync(worker);
        return Results.Ok();
    }

    private static async Task<IResult> UpdateConfig(Guid id, [FromBody] UpdateWorkerConfigRequest req, IFleetStorage storage)
    {
        var worker = await storage.GetWorkerAsync(id);
        if (worker is null) return Results.NotFound();

        if (req.MaxDiskUsageGb.HasValue)
            worker.MaxDiskUsageBytes = (long)(req.MaxDiskUsageGb.Value * 1024 * 1024 * 1024);
        if (req.RepoStoragePath is not null)
            worker.RepoStoragePath = req.RepoStoragePath;

        await storage.UpdateWorkerAsync(worker);
        return Results.Ok(worker);
    }

    private static async Task<IResult> UpdateStatus(
        Guid id,
        [FromBody] UpdateWorkerStatusRequest req,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetClaimedWorkerId(httpContext, out var claimed) || claimed != id)
            return Results.Forbid();

        var worker = await storage.GetWorkerAsync(id);
        if (worker is null) return Results.NotFound();

        worker.Status = req.Status;
        worker.LastSeenAt = DateTimeOffset.UtcNow;
        await storage.UpdateWorkerAsync(worker);
        return Results.Ok();
    }

    private static async Task<IResult> GetSelf(HttpContext httpContext, IFleetStorage storage)
    {
        if (!TryGetClaimedWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var worker = await storage.GetWorkerAsync(workerId);
        return worker is null ? Results.NotFound() : Results.Ok(worker);
    }

    private static async Task<IResult> GetRepoCaches(
        Guid id,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetClaimedWorkerId(httpContext, out var claimed) || claimed != id)
            return Results.Forbid();

        var caches = await storage.GetRepoCachesAsync(id);
        return Results.Ok(caches);
    }

    private static async Task<IResult> UpsertRepoCache(
        Guid id,
        [FromBody] RepoCache cache,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetClaimedWorkerId(httpContext, out var claimed) || claimed != id)
            return Results.Forbid();

        cache.WorkerId = id;
        await storage.UpsertRepoCacheAsync(cache);
        return Results.Ok(cache);
    }

    private static async Task<IResult> DeleteRepoCache(
        Guid id,
        Guid cacheId,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetClaimedWorkerId(httpContext, out var claimed) || claimed != id)
            return Results.Forbid();

        await storage.DeleteRepoCacheAsync(cacheId);
        return Results.NoContent();
    }

    private static bool TryGetClaimedWorkerId(HttpContext ctx, out Guid workerId)
    {
        var raw = ctx.User.FindFirst("worker_id")?.Value;
        return Guid.TryParse(raw, out workerId);
    }

    public record RegisterWorkerRequest(string Name, bool IsEmbedded = false, double? MaxDiskUsageGb = null, string? RepoStoragePath = null);
    public record UpdateWorkerConfigRequest(double? MaxDiskUsageGb, string? RepoStoragePath);
    public record WorkerLoginRequest(Guid WorkerId, string Secret);
    public record UpdateWorkerStatusRequest(WorkerStatus Status);
}
