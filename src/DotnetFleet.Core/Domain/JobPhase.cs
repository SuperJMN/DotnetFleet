namespace DotnetFleet.Core.Domain;

/// <summary>
/// A persisted phase entry for a deployment job. One row per
/// <c>phase.start</c>; <see cref="EndedAt"/> is filled in when the matching
/// <c>phase.end</c> arrives. Pure-info markers (<c>phase.info</c>) are stored
/// with <see cref="EndedAt"/> equal to <see cref="StartedAt"/> and a populated
/// <see cref="Message"/>.
/// </summary>
public class JobPhase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public PhaseStatus Status { get; set; } = PhaseStatus.Unknown;
    public long? DurationMs { get; set; }
    public string? Message { get; set; }

    /// <summary>
    /// Free-form attributes serialized as JSON (e.g. <c>asset</c>, <c>arch</c>,
    /// <c>project</c>). Stored verbatim from the marker; consumers parse on read.
    /// </summary>
    public string? AttrsJson { get; set; }
}
