using DotnetFleet.Core.Domain;

namespace DotnetFleet.Tests;

public class DeploymentJobDurationTests
{
    [Fact]
    public void MarkFinished_ShouldPersistTotalElapsedTimeFromEnqueue()
    {
        var enqueuedAt = new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero);
        var job = new DeploymentJob
        {
            EnqueuedAt = enqueuedAt,
            StartedAt = enqueuedAt.AddMinutes(1),
            CurrentPhase = "worker.deployer.invoke",
            CurrentPhaseStartedAt = enqueuedAt.AddMinutes(2)
        };

        job.MarkFinished(enqueuedAt.AddMinutes(3).AddSeconds(5));

        job.FinishedAt.Should().Be(enqueuedAt.AddMinutes(3).AddSeconds(5));
        job.TotalDurationMs.Should().Be(185_000);
        job.CurrentPhase.Should().BeNull();
        job.CurrentPhaseStartedAt.Should().BeNull();
    }

    [Fact]
    public void GetElapsedDurationMs_ShouldUsePersistedDurationForFinishedJobs()
    {
        var enqueuedAt = new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero);
        var job = new DeploymentJob
        {
            EnqueuedAt = enqueuedAt,
            FinishedAt = enqueuedAt.AddSeconds(30),
            TotalDurationMs = 30_000
        };

        job.GetElapsedDurationMs(enqueuedAt.AddHours(1)).Should().Be(30_000);
    }

    [Fact]
    public void GetElapsedDurationMs_ShouldClampClockSkewAtZero()
    {
        var enqueuedAt = new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero);
        var job = new DeploymentJob { EnqueuedAt = enqueuedAt };

        job.GetElapsedDurationMs(enqueuedAt.AddSeconds(-10)).Should().Be(0);
    }
}
