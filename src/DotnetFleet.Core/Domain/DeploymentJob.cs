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

    /// <summary>
    /// Human-friendly version of the artifact being deployed (e.g. "1.2.3-beta.4").
    /// Detected on the fly by scanning incoming log lines for GitVersion / NBGV / MinVer
    /// output. Null until the first version line is observed; once set, it is not overwritten.
    /// </summary>
    public string? Version { get; set; }
}
