using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// In-memory pub/sub for real-time log streaming via SSE.
/// Workers publish lines; SSE handlers subscribe to receive them.
/// All subscriber-list access is synchronized via lock.
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

        var list = subscribers.GetOrAdd(jobId, _ => []);
        lock (list) { list.Add(channel); }
        return channel;
    }

    public void Unsubscribe(Guid jobId, Channel<string> channel)
    {
        if (subscribers.TryGetValue(jobId, out var list))
            lock (list) { list.Remove(channel); }
    }

    public void Publish(Guid jobId, string line)
    {
        if (!subscribers.TryGetValue(jobId, out var list)) return;

        Channel<string>[] snapshot;
        lock (list) { snapshot = [.. list]; }

        foreach (var channel in snapshot)
            channel.Writer.TryWrite(line);
    }

    public void Complete(Guid jobId)
    {
        if (!subscribers.TryRemove(jobId, out var list)) return;

        Channel<string>[] snapshot;
        lock (list) { snapshot = [.. list]; }

        foreach (var channel in snapshot)
            channel.Writer.TryComplete();
    }
}
