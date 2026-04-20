namespace DotnetFleet.Core.Domain;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string GitUrl { get; set; } = "";
    public string Branch { get; set; } = "main";

    /// <summary>Minutes between automatic polling checks. 0 = disabled.</summary>
    public int PollingIntervalMinutes { get; set; } = 0;

    public string? LastPolledCommitSha { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
