namespace DotnetFleet.Core.Domain;

public class Worker
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Hashed shared secret used for worker → coordinator authentication.</summary>
    public string SecretHash { get; set; } = "";

    public WorkerStatus Status { get; set; } = WorkerStatus.Offline;
    public bool IsEmbedded { get; set; } = false;
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Maximum allowed disk usage for cached repos (bytes). 0 = unlimited.</summary>
    public long MaxDiskUsageBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB default

    public string? RepoStoragePath { get; set; }
}
