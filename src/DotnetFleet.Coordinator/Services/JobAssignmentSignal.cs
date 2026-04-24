using System.Threading.Channels;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// Coalescing wake-up signal for the <see cref="JobAssignmentService"/>. Endpoints
/// (enqueue, complete) and infra (worker came online) call <see cref="Notify"/> after
/// a state change that may unblock new assignments. The service drains the channel and
/// runs at most one pass per signal — multiple notifications between ticks coalesce.
/// </summary>
public class JobAssignmentSignal
{
    // Capacity 1 + DropWrite is the standard "edge-triggered" pattern: once a wake-up is
    // queued, additional Notify() calls in the same window are no-ops. The consumer will
    // re-check storage on its next pass anyway, so we never lose work.
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => channel.Writer.TryWrite(true);

    internal ChannelReader<bool> Reader => channel.Reader;
}
