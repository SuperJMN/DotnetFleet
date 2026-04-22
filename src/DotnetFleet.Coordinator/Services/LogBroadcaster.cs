using System.Collections.Concurrent;
using System.Threading.Channels;
using DotnetFleet.Core.Domain;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// In-memory pub/sub for real-time log streaming via SSE.
/// Workers publish entries; SSE handlers subscribe to receive them.
/// Entries carry their persisted <see cref="LogEntry.Id"/> so subscribers
/// can dedupe against logs they already loaded from storage.
/// All subscriber-list access is synchronized via lock.
/// </summary>
public class LogBroadcaster
{
    private readonly ConcurrentDictionary<Guid, List<Channel<LogEntry>>> subscribers = new();

    public Channel<LogEntry> Subscribe(Guid jobId)
    {
        var channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var list = subscribers.GetOrAdd(jobId, _ => []);
        lock (list) { list.Add(channel); }
        return channel;
    }

    public void Unsubscribe(Guid jobId, Channel<LogEntry> channel)
    {
        if (subscribers.TryGetValue(jobId, out var list))
            lock (list) { list.Remove(channel); }
    }

    public void Publish(Guid jobId, LogEntry entry)
    {
        if (!subscribers.TryGetValue(jobId, out var list)) return;

        Channel<LogEntry>[] snapshot;
        lock (list) { snapshot = [.. list]; }

        foreach (var channel in snapshot)
            channel.Writer.TryWrite(entry);
    }

    public void Complete(Guid jobId)
    {
        if (!subscribers.TryRemove(jobId, out var list)) return;

        Channel<LogEntry>[] snapshot;
        lock (list) { snapshot = [.. list]; }

        foreach (var channel in snapshot)
            channel.Writer.TryComplete();
    }
}
