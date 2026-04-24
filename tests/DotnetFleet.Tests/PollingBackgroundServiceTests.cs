using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using DotnetFleet.Coordinator.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DotnetFleet.Tests;

/// <summary>
/// Tests for the commit-detection and auto-enqueue logic inside PollingBackgroundService.
/// We test the internal helper by subclassing and exposing it.
/// </summary>
public class PollingBackgroundServiceTests
{
    // Expose protected internals via thin subclass
    private sealed class TestablePoller : PollingBackgroundService
    {
        public TestablePoller(IServiceScopeFactory scopeFactory)
            : base(scopeFactory, NullLogger<PollingBackgroundService>.Instance, new JobAssignmentSignal()) { }

        public Task PollAllAsync(CancellationToken ct) => PollAllProjectsAsync(ct);
    }

    private static (TestablePoller poller, IFleetStorage storage) Build(IEnumerable<Project> projects)
    {
        var storage = Substitute.For<IFleetStorage>();
        storage.GetProjectsAsync().ReturnsForAnyArgs(projects.ToList());

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.GetService(typeof(IFleetStorage)).Returns(storage);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);

        return (new TestablePoller(factory), storage);
    }

    [Fact]
    public async Task Project_with_zero_polling_interval_is_skipped()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "x", GitUrl = "https://example.com/repo.git",
            Branch = "main", PollingIntervalMinutes = 0
        };
        var (poller, storage) = Build([project]);

        await poller.PollAllAsync(CancellationToken.None);

        await storage.DidNotReceiveWithAnyArgs().AddJobAsync(default!, default);
    }

    [Fact]
    public async Task Project_polled_too_recently_is_skipped()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "x", GitUrl = "https://example.com/repo.git",
            Branch = "main", PollingIntervalMinutes = 60,
            // Last polled 5 minutes ago — next poll in 55 min
            LastPolledAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var (poller, storage) = Build([project]);

        await poller.PollAllAsync(CancellationToken.None);

        await storage.DidNotReceiveWithAnyArgs().AddJobAsync(default!, default);
    }
}
