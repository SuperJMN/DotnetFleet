namespace DotnetFleet.Core.Domain;

/// <summary>
/// Per (project, worker) exponentially-weighted moving average of successful deployment
/// durations. Updated whenever a job succeeds. Used by the smart scheduler to decide
/// which worker yields the lowest expected time-to-finish for a new job.
/// </summary>
public class JobDurationStat
{
    public Guid ProjectId { get; set; }
    public Guid WorkerId { get; set; }

    /// <summary>EWMA of the wall-clock duration in milliseconds.</summary>
    public double EwmaMs { get; set; }

    /// <summary>Number of samples observed so far (informational, useful for cold-start tuning).</summary>
    public int Samples { get; set; }

    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
}
