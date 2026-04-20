namespace DotnetFleet.Core.Domain;

public class LogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Line { get; set; } = "";
}
