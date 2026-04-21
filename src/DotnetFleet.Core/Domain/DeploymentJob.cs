namespace DotnetFleet.Core.Domain;

public class DeploymentJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? WorkerId { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>Commit SHA that triggered this job (null for manual deploys).</summary>
    public string? TriggerCommitSha { get; set; }

    public bool IsAutoTriggered { get; set; } = false;
    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Set when a user requests cancellation. Workers poll this to abort in-flight jobs.</summary>
    public DateTimeOffset? CancellationRequestedAt { get; set; }
}
