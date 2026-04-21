using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Channels;

namespace DotnetFleet.Coordinator.Endpoints;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs").RequireAuthorization();

        group.MapGet("/", GetAll);
        group.MapGet("/{id:guid}", GetById);
        group.MapGet("/{id:guid}/logs", StreamLogs);
        group.MapPost("/{id:guid}/cancel", CancelJob);

        // Worker internal endpoints (authenticated by worker secret header)
        var workerGroup = app.MapGroup("/api/queue");
        workerGroup.MapGet("/next", GetNextJob).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/start", ReportStarted).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/logs", AppendLogs).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/complete", ReportCompleted).RequireAuthorization("Worker");
        workerGroup.MapGet("/jobs/{id:guid}/should-cancel", ShouldCancel).RequireAuthorization("Worker");
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
            || job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

        if (isTerminal) return;

        // Subscribe to new log lines (only for running / queued jobs)
        var channel = broadcaster.Subscribe(id);
        try
        {
            var heartbeatInterval = TimeSpan.FromSeconds(15);
            while (!ct.IsCancellationRequested)
            {
                var readTask = channel.Reader.ReadAsync(ct).AsTask();
                var delayTask = Task.Delay(heartbeatInterval, ct);
                var winner = await Task.WhenAny(readTask, delayTask);

                if (winner == readTask)
                {
                    var line = await readTask;
                    await WriteEventAsync(httpContext.Response, line, ct);
                }
                else
                {
                    // Heartbeat: SSE comment line, ignored by clients but keeps proxies/intermediaries
                    // from closing the idle connection.
                    await httpContext.Response.WriteAsync(": keep-alive\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (ChannelClosedException) { /* job finished */ }
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
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var job = await storage.ClaimNextJobAsync(workerId);
        return job is null ? Results.NoContent() : Results.Ok(job);
    }

    private static async Task<IResult> ReportStarted(
        Guid id,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();
        if (job.WorkerId != workerId) return Results.Forbid();

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await storage.UpdateJobAsync(job);
        return Results.Ok();
    }

    private static async Task<IResult> AppendLogs(
        Guid id,
        [FromBody] AppendLogsRequest req,
        HttpContext httpContext,
        IFleetStorage storage,
        LogBroadcaster broadcaster)
    {
        if (!TryGetWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();
        if (job.WorkerId != workerId) return Results.Forbid();

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
        HttpContext httpContext,
        IFleetStorage storage,
        LogBroadcaster broadcaster)
    {
        if (!TryGetWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();
        if (job.WorkerId != workerId) return Results.Forbid();

        job.Status = job.CancellationRequestedAt is not null && !req.Success
            ? JobStatus.Cancelled
            : req.Success ? JobStatus.Succeeded : JobStatus.Failed;
        job.FinishedAt = DateTimeOffset.UtcNow;
        job.ErrorMessage = req.ErrorMessage;
        await storage.UpdateJobAsync(job);

        broadcaster.Complete(id);
        return Results.Ok();
    }

    public record AppendLogsRequest(string[] Lines);
    public record CompleteJobRequest(bool Success, string? ErrorMessage);

    private static async Task<IResult> CancelJob(
        Guid id,
        IFleetStorage storage,
        LogBroadcaster broadcaster)
    {
        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();

        if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
            return Results.Conflict(new { message = "Job is already in a terminal state." });

        job.CancellationRequestedAt = DateTimeOffset.UtcNow;

        if (job.Status is JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.FinishedAt = DateTimeOffset.UtcNow;
            broadcaster.Complete(id);
        }

        await storage.UpdateJobAsync(job);
        return Results.Ok(job);
    }

    private static async Task<IResult> ShouldCancel(Guid id, IFleetStorage storage)
    {
        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();
        return Results.Ok(new { shouldCancel = job.CancellationRequestedAt is not null });
    }

    private static bool TryGetWorkerId(HttpContext ctx, out Guid workerId)
    {
        var raw = ctx.User.FindFirst("worker_id")?.Value;
        return Guid.TryParse(raw, out workerId);
    }
}
