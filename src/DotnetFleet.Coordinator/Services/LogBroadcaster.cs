using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// In-memory pub/sub for real-time log streaming via SSE.
/// Workers publish lines; SSE handlers subscribe to receive them.
/// </summary>
public class LogBroadcaster
{
    private readonly ConcurrentDictionary<Guid, List<Channel<string>>> subscribers = new();

    public Channel<string> Subscribe(Guid jobId)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        subscribers.GetOrAdd(jobId, _ => []).Add(channel);
        return channel;
    }

    public void Unsubscribe(Guid jobId, Channel<string> channel)
    {
        if (subscribers.TryGetValue(jobId, out var list))
            list.Remove(channel);
    }

    public void Publish(Guid jobId, string line)
    {
        if (!subscribers.TryGetValue(jobId, out var list)) return;
        foreach (var channel in list)
            channel.Writer.TryWrite(line);
    }

    public void Complete(Guid jobId)
    {
        if (!subscribers.TryGetValue(jobId, out var list)) return;
        foreach (var channel in list)
            channel.Writer.TryComplete();
        subscribers.TryRemove(jobId, out _);
    }
}
