namespace DotnetFleet.Core.Domain;

/// <summary>
/// Action a worker should take while executing a job, as instructed by the coordinator
/// on each cancellation poll. The coordinator is the source of truth for job lifecycle;
/// this enum is how that truth is conveyed back to in-flight workers.
/// </summary>
public enum JobAction
{
    /// <summary>Keep working — nothing has changed on the coordinator side.</summary>
    Continue = 0,

    /// <summary>A user has requested cancellation. The worker should stop gracefully and report completion.</summary>
    Cancel = 1,

    /// <summary>
    /// The coordinator no longer expects this worker to be running this job
    /// (terminal state, ownership lost, job deleted, …). The worker MUST give up
    /// immediately and MUST NOT attempt to report completion — doing so would either
    /// 404 or rewrite a terminal state.
    /// </summary>
    Abort = 2
}
