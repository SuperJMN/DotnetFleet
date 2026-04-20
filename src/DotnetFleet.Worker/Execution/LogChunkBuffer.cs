using System.Diagnostics;

namespace DotnetFleet.WorkerService.Execution;

/// <summary>
/// Thread-safe log buffer that flushes to <paramref name="send"/> when either:
/// <list type="bullet">
///   <item>the buffer reaches <paramref name="thresholdLines"/> lines, or</item>
///   <item><paramref name="debounce"/> elapses since the last appended line.</item>
/// </list>
/// <see cref="DisposeAsync"/> performs a final flush.
/// </summary>
public sealed class LogChunkBuffer : IAsyncDisposable
{
    private readonly Func<string[], CancellationToken, Task> send;
    private readonly int thresholdLines;
    private readonly TimeSpan debounce;
    private readonly CancellationToken ct;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<string> buffer = new();
    private CancellationTokenSource? pendingFlushCts;
    private Task? pendingFlushTask;

    public LogChunkBuffer(
        Func<string[], CancellationToken, Task> send,
        CancellationToken ct,
        int thresholdLines = 20,
        TimeSpan? debounce = null)
    {
        this.send = send;
        this.ct = ct;
        this.thresholdLines = thresholdLines;
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task AppendAsync(string line)
    {
        await gate.WaitAsync(ct);
        try
        {
            buffer.Add(line);
            if (buffer.Count >= thresholdLines)
            {
                await FlushLockedAsync();
                return;
            }

            // Reset/start the debounce timer.
            pendingFlushCts?.Cancel();
            pendingFlushCts?.Dispose();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pendingFlushCts = cts;
            pendingFlushTask = ScheduleFlushAsync(cts.Token);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task FlushAsync()
    {
        await gate.WaitAsync(ct);
        try
        {
            await FlushLockedAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task FlushLockedAsync()
    {
        pendingFlushCts?.Cancel();
        if (buffer.Count == 0) return;
        var chunk = buffer.ToArray();
        buffer.Clear();
        await send(chunk, ct);
    }

    private async Task ScheduleFlushAsync(CancellationToken flushCt)
    {
        try
        {
            await Task.Delay(debounce, flushCt);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            await FlushLockedAsync();
        }
        catch
        {
            // Swallow: caller will report job failure via other paths.
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushAsync();
        }
        catch { /* best effort */ }

        pendingFlushCts?.Cancel();
        pendingFlushCts?.Dispose();
        gate.Dispose();
    }
}
