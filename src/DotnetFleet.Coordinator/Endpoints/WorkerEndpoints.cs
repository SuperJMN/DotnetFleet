using static BCrypt.Net.BCrypt;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DotnetFleet.Coordinator.Endpoints;

public static class WorkerEndpoints
{
    public static void MapWorkerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workers").RequireAuthorization();

        group.MapGet("/", GetAll);
        group.MapPost("/register", Register);
        group.MapPut("/{id:guid}/heartbeat", Heartbeat).RequireAuthorization("Worker");
        group.MapPut("/{id:guid}/config", UpdateConfig);
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

    private static async Task<IResult> Register([FromBody] RegisterWorkerRequest req, IFleetStorage storage)
    {
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

    private static async Task<IResult> Heartbeat(
        Guid id,
        [FromHeader(Name = "X-Worker-Id")] Guid headerId,
        IFleetStorage storage)
    {
        var worker = await storage.GetWorkerAsync(id);
        if (worker is null) return Results.NotFound();

        worker.LastSeenAt = DateTimeOffset.UtcNow;
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

    public record RegisterWorkerRequest(string Name, bool IsEmbedded = false, double? MaxDiskUsageGb = null, string? RepoStoragePath = null);
    public record UpdateWorkerConfigRequest(double? MaxDiskUsageGb, string? RepoStoragePath);
}
