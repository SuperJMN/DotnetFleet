using DotnetFleet.Coordinator.Services;
using FluentAssertions;

namespace DotnetFleet.Tests;

public class LogBroadcasterTests
{
    [Fact]
    public async Task Published_line_is_received_by_subscriber()
    {
        var broadcaster = new LogBroadcaster();
        var jobId = Guid.NewGuid();
        var channel = broadcaster.Subscribe(jobId);

        broadcaster.Publish(jobId, "hello");
        broadcaster.Complete(jobId);

        var lines = new List<string>();
        await foreach (var line in channel.Reader.ReadAllAsync())
            lines.Add(line);

        lines.Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public async Task Multiple_subscribers_all_receive_published_lines()
    {
        var broadcaster = new LogBroadcaster();
        var jobId = Guid.NewGuid();

        var ch1 = broadcaster.Subscribe(jobId);
        var ch2 = broadcaster.Subscribe(jobId);

        broadcaster.Publish(jobId, "line1");
        broadcaster.Publish(jobId, "line2");
        broadcaster.Complete(jobId);

        var lines1 = new List<string>();
        await foreach (var l in ch1.Reader.ReadAllAsync()) lines1.Add(l);

        var lines2 = new List<string>();
        await foreach (var l in ch2.Reader.ReadAllAsync()) lines2.Add(l);

        lines1.Should().Equal("line1", "line2");
        lines2.Should().Equal("line1", "line2");
    }

    [Fact]
    public void Publish_to_unknown_job_does_not_throw()
    {
        var broadcaster = new LogBroadcaster();
        var act = () => broadcaster.Publish(Guid.NewGuid(), "ignored");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Unsubscribed_channel_does_not_receive_further_lines()
    {
        var broadcaster = new LogBroadcaster();
        var jobId = Guid.NewGuid();
        var channel = broadcaster.Subscribe(jobId);

        broadcaster.Publish(jobId, "before-unsub");
        broadcaster.Unsubscribe(jobId, channel);
        broadcaster.Publish(jobId, "after-unsub");
        broadcaster.Complete(jobId);

        // The channel writer was not completed by Unsubscribe, so we only
        // read what was buffered before unsubscribing.
        var received = new List<string>();
        while (channel.Reader.TryRead(out var line))
            received.Add(line);

        received.Should().ContainSingle("before-unsub");
    }

    [Fact]
    public async Task Complete_closes_channel_reader()
    {
        var broadcaster = new LogBroadcaster();
        var jobId = Guid.NewGuid();
        var channel = broadcaster.Subscribe(jobId);

        broadcaster.Complete(jobId);

        var lines = new List<string>();
        await foreach (var l in channel.Reader.ReadAllAsync())
            lines.Add(l);

        lines.Should().BeEmpty();
    }
}
