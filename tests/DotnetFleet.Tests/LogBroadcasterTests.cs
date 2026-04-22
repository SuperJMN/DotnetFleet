using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using FluentAssertions;

namespace DotnetFleet.Tests;

public class LogBroadcasterTests
{
    private static LogEntry Entry(Guid jobId, string line) =>
        new() { JobId = jobId, Line = line };

    [Fact]
    public async Task Published_line_is_received_by_subscriber()
    {
        var broadcaster = new LogBroadcaster();
        var jobId = Guid.NewGuid();
        var channel = broadcaster.Subscribe(jobId);

        broadcaster.Publish(jobId, Entry(jobId, "hello"));
        broadcaster.Complete(jobId);

        var lines = new List<string>();
        await foreach (var entry in channel.Reader.ReadAllAsync())
            lines.Add(entry.Line);

        lines.Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public async Task Multiple_subscribers_all_receive_published_lines()
    {
        var broadcaster = new LogBroadcaster();
        var jobId = Guid.NewGuid();

        var ch1 = broadcaster.Subscribe(jobId);
        var ch2 = broadcaster.Subscribe(jobId);

        broadcaster.Publish(jobId, Entry(jobId, "line1"));
        broadcaster.Publish(jobId, Entry(jobId, "line2"));
        broadcaster.Complete(jobId);

        var lines1 = new List<string>();
        await foreach (var e in ch1.Reader.ReadAllAsync()) lines1.Add(e.Line);

        var lines2 = new List<string>();
        await foreach (var e in ch2.Reader.ReadAllAsync()) lines2.Add(e.Line);

        lines1.Should().Equal("line1", "line2");
        lines2.Should().Equal("line1", "line2");
    }

    [Fact]
    public void Publish_to_unknown_job_does_not_throw()
    {
        var broadcaster = new LogBroadcaster();
        var act = () => broadcaster.Publish(Guid.NewGuid(), Entry(Guid.NewGuid(), "ignored"));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Unsubscribed_channel_does_not_receive_further_lines()
    {
        var broadcaster = new LogBroadcaster();
        var jobId = Guid.NewGuid();
        var channel = broadcaster.Subscribe(jobId);

        broadcaster.Publish(jobId, Entry(jobId, "before-unsub"));
        broadcaster.Unsubscribe(jobId, channel);
        broadcaster.Publish(jobId, Entry(jobId, "after-unsub"));
        broadcaster.Complete(jobId);

        // The channel writer was not completed by Unsubscribe, so we only
        // read what was buffered before unsubscribing.
        var received = new List<string>();
        while (channel.Reader.TryRead(out var entry))
            received.Add(entry.Line);

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
        await foreach (var e in channel.Reader.ReadAllAsync())
            lines.Add(e.Line);

        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_publish_and_subscribe_does_not_throw()
    {
        var broadcaster = new LogBroadcaster();
        var jobId = Guid.NewGuid();

        // Hammer the broadcaster from multiple threads simultaneously.
        // Before the lock fix this would throw InvalidOperationException
        // ("Collection was modified during enumeration").
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var publishTask = Task.Run(async () =>
        {
            for (var i = 0; i < 500 && !cts.IsCancellationRequested; i++)
                broadcaster.Publish(jobId, Entry(jobId, $"line-{i}"));
        });

        var subscribeTask = Task.Run(async () =>
        {
            var channels = new List<System.Threading.Channels.Channel<LogEntry>>();
            for (var i = 0; i < 500 && !cts.IsCancellationRequested; i++)
            {
                var ch = broadcaster.Subscribe(jobId);
                channels.Add(ch);
                if (i % 3 == 0)
                    broadcaster.Unsubscribe(jobId, ch);
            }
        });

        var act = () => Task.WhenAll(publishTask, subscribeTask);
        await act.Should().NotThrowAsync();
    }
}
