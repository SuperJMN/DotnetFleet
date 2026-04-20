using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DotnetFleet.Coordinator.Endpoints;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs").RequireAuthorization();

        group.MapGet("/", GetAll);
        group.MapGet("/{id:guid}", GetById);
        group.MapGet("/{id:guid}/logs", StreamLogs);

        // Worker internal endpoints (authenticated by worker secret header)
        var workerGroup = app.MapGroup("/api/queue");
        workerGroup.MapGet("/next", GetNextJob).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/start", ReportStarted).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/logs", AppendLogs).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/complete", ReportCompleted).RequireAuthorization("Worker");
    }

    private static async Task<IResult> GetAll(IFleetStorage storage)
    {
        var jobs = await storage.GetJobsAsync();
        return Results.Ok(jobs);
    }

    private static async Task<IResult> GetById(Guid id, IFleetStorage storage)
    {
        var job = await storage.GetJobAsync(id);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }

    /// <summary>
    /// Server-Sent Events stream of log lines for a given job.
    /// Existing logs are sent first, then new lines are pushed as they arrive.
    /// For completed jobs the stream closes immediately after existing logs.
    /// </summary>
    private static async Task StreamLogs(
        Guid id,
        HttpContext httpContext,
        IFleetStorage storage,
        LogBroadcaster broadcaster,
        CancellationToken ct)
    {
        httpContext.Response.Headers["Content-Type"] = "text/event-stream";
        httpContext.Response.Headers["Cache-Control"] = "no-cache";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        await httpContext.Response.Body.FlushAsync(ct);

        // Send existing logs first
        var existing = await storage.GetLogsAsync(id, ct);
        foreach (var entry in existing)
        {
            await WriteEventAsync(httpContext.Response, entry.Line, ct);
        }

        // If job is already in a terminal state, close the stream
        var job = await storage.GetJobAsync(id, ct);
        bool isTerminal = job is null
            || job.Status is JobStatus.Succeeded or JobStatus.Failed;

        if (isTerminal) return;

        // Subscribe to new log lines (only for running / queued jobs)
        var channel = broadcaster.Subscribe(id);
        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(ct))
            {
                await WriteEventAsync(httpContext.Response, line, ct);
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        finally
        {
            broadcaster.Unsubscribe(id, channel);
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, string data, CancellationToken ct)
    {
        await response.WriteAsync($"data: {data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    // Worker-facing endpoints

    private static async Task<IResult> GetNextJob(
        [FromHeader(Name = "X-Worker-Id")] Guid workerId,
        IFleetStorage storage)
    {
        var job = await storage.DequeueNextJobAsync();
        if (job is null)
            return Results.NoContent();

        job.WorkerId = workerId;
        job.Status = JobStatus.Assigned;
        await storage.UpdateJobAsync(job);

        return Results.Ok(job);
    }

    private static async Task<IResult> ReportStarted(
        Guid id,
        [FromHeader(Name = "X-Worker-Id")] Guid workerId,
        IFleetStorage storage)
    {
        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        job.WorkerId = workerId;
        await storage.UpdateJobAsync(job);
        return Results.Ok();
    }

    private static async Task<IResult> AppendLogs(
        Guid id,
        [FromBody] AppendLogsRequest req,
        IFleetStorage storage,
        LogBroadcaster broadcaster)
    {
        var entries = req.Lines.Select(line => new LogEntry
        {
            JobId = id,
            Line = line,
            Timestamp = DateTimeOffset.UtcNow
        }).ToList();

        await storage.AddLogEntriesAsync(entries);

        foreach (var line in req.Lines)
            broadcaster.Publish(id, line);

        return Results.Ok();
    }

    private static async Task<IResult> ReportCompleted(
        Guid id,
        [FromBody] CompleteJobRequest req,
        IFleetStorage storage,
        LogBroadcaster broadcaster)
    {
        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();

        job.Status = req.Success ? JobStatus.Succeeded : JobStatus.Failed;
        job.FinishedAt = DateTimeOffset.UtcNow;
        job.ErrorMessage = req.ErrorMessage;
        await storage.UpdateJobAsync(job);

        broadcaster.Complete(id);
        return Results.Ok();
    }

    public record AppendLogsRequest(string[] Lines);
    public record CompleteJobRequest(bool Success, string? ErrorMessage);
}
