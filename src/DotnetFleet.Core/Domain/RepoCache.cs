namespace DotnetFleet.Core.Domain;

/// <summary>Tracks a locally cached git repository on a worker.</summary>
public class RepoCache
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkerId { get; set; }
    public Guid ProjectId { get; set; }
    public string LocalPath { get; set; } = "";
    public long SizeBytes { get; set; } = 0;
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastKnownCommitSha { get; set; }
}
