namespace DotnetFleet.Core.Domain;

public enum JobStatus
{
    Queued,
    Assigned,
    Running,
    Succeeded,
    Failed,
    Cancelled
}
