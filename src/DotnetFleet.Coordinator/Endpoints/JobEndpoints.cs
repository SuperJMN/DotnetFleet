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
        var workerGroup = app.MapGroup("/api/queue").AddEndpointFilter<WorkerLivenessFilter>();
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
    /// While the job is waiting to be picked up, periodic status events are sent
    /// so the client can show feedback (e.g. "Waiting for an available worker…").
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

        // Subscribe BEFORE reading existing logs so no messages are lost
        // in the window between the DB read and the subscription.
        var channel = broadcaster.Subscribe(id);
        var seenLogIds = new HashSet<Guid>();
        try
        {
            // Send existing logs first
            var existing = await storage.GetLogsAsync(id, ct);
            foreach (var entry in existing)
            {
                seenLogIds.Add(entry.Id);
                await WriteEventAsync(httpContext.Response, entry.Line, ct);
            }

            // If job is already in a terminal state, close the stream
            var job = await storage.GetJobAsync(id, ct);
            bool isTerminal = job is null
                || job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

            if (isTerminal)
            {
                await WriteStatusEventAsync(httpContext.Response, job?.Status ?? JobStatus.Failed, ct);
                return;
            }

            // Send initial status so the client shows feedback immediately
            var lastKnownStatus = job!.Status;
            await WriteStatusEventAsync(httpContext.Response, lastKnownStatus, ct);

            if (lastKnownStatus == JobStatus.Queued)
                await WriteEventAsync(httpContext.Response, "⏳ Waiting for an available worker…", ct);

            // Stream new log lines as they arrive, polling job status on each heartbeat
            // so the client gets timely feedback during the Queued→Running transition.
            var statusPollInterval = TimeSpan.FromSeconds(5);
            // Convert ValueTask to Task exactly once per ReadAsync call.
            // ValueTask must never be awaited / AsTask'd more than once.
            var pendingRead = channel.Reader.ReadAsync(ct).AsTask();
            while (!ct.IsCancellationRequested)
            {
                var delayTask = Task.Delay(statusPollInterval, ct);
                var winner = await Task.WhenAny(pendingRead, delayTask);

                if (winner == pendingRead)
                {
                    var entry = await pendingRead;
                    if (seenLogIds.Add(entry.Id))
                        await WriteEventAsync(httpContext.Response, entry.Line, ct);
                    pendingRead = channel.Reader.ReadAsync(ct).AsTask();
                }
                else
                {
                    // Poll job status — send an update when it changes, otherwise keep-alive
                    var current = await storage.GetJobAsync(id, ct);

                    if (current is null
                        || current.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
                    {
                        await WriteStatusEventAsync(httpContext.Response, current?.Status ?? JobStatus.Failed, ct);
                        break;
                    }

                    if (current.Status != lastKnownStatus)
                    {
                        lastKnownStatus = current.Status;
                        await WriteStatusEventAsync(httpContext.Response, lastKnownStatus, ct);

                        var transitionMsg = lastKnownStatus switch
                        {
                            JobStatus.Assigned => "🔄 Worker assigned, preparing deployment…",
                            JobStatus.Running => "🚀 Deployment started",
                            _ => null
                        };
                        if (transitionMsg is not null)
                            await WriteEventAsync(httpContext.Response, transitionMsg, ct);
                    }
                    else
                    {
                        await httpContext.Response.WriteAsync(": keep-alive\n\n", ct);
                        await httpContext.Response.Body.FlushAsync(ct);
                    }
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

    private static async Task WriteStatusEventAsync(HttpResponse response, JobStatus status, CancellationToken ct)
    {
        await response.WriteAsync($"event: status\ndata: {status}\n\n", ct);
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
        LogBroadcaster broadcaster,
        ILoggerFactory loggerFactory)
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

        foreach (var entry in entries)
            broadcaster.Publish(id, entry);

        // Version detection is best-effort and must NEVER fail the log-append:
        // logs are already persisted (line above) and broadcast. If the tracker
        // throws (e.g. transient DB lock, malformed line, etc.) we log a warning
        // and return 200 so the worker doesn't retry and duplicate the batch.
        try
        {
            await DeploymentVersionTracker.TryUpdateVersionAsync(storage, job, req.Lines);
        }
        catch (Exception ex)
        {
            loggerFactory
                .CreateLogger(typeof(JobEndpoints).FullName!)
                .LogWarning(ex, "Deployment version detection failed for job {JobId}; logs were persisted regardless.", id);
        }

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

    private static async Task<IResult> ShouldCancel(Guid id, HttpContext ctx, IFleetStorage storage)
    {
        if (!TryGetWorkerId(ctx, out var callerWorkerId))
            return Results.Forbid();

        var job = await storage.GetJobAsync(id);

        // Job no longer exists or has reached a terminal state — the worker must
        // abort whatever in-memory state it still holds for it. The coordinator has
        // already moved on; reporting completion would either 404 or rewrite a
        // terminal state.
        if (job is null
            || job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
        {
            return Results.Ok(new
            {
                shouldCancel = false,
                action = nameof(JobAction.Abort)
            });
        }

        // Ownership lost (e.g. reaper failed the job and it was re-queued and claimed
        // by another worker). The current worker must release it.
        if (job.WorkerId is not null && job.WorkerId != callerWorkerId)
        {
            return Results.Ok(new
            {
                shouldCancel = false,
                action = nameof(JobAction.Abort)
            });
        }

        var cancelRequested = job.CancellationRequestedAt is not null;
        return Results.Ok(new
        {
            shouldCancel = cancelRequested,
            action = (cancelRequested ? JobAction.Cancel : JobAction.Continue).ToString()
        });
    }

    private static bool TryGetWorkerId(HttpContext ctx, out Guid workerId)
    {
        var raw = ctx.User.FindFirst("worker_id")?.Value;
        return Guid.TryParse(raw, out workerId);
    }
}
