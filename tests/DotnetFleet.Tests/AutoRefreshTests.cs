using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using DotnetFleet.ViewModels;
using ReactiveUI;
using ReactiveUI.Builder;

namespace DotnetFleet.Tests;

public class AutoRefreshTests
{
    static AutoRefreshTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public void Start_runs_enabled_ticks_and_pauses_when_disabled()
    {
        using var enabled = new BehaviorSubject<bool>(false);
        using var ticks = new Subject<long>();
        var count = 0;
        var command = ReactiveCommand.CreateFromTask(() =>
        {
            count++;
            return Task.CompletedTask;
        });

        using var subscription = AutoRefresh.Start(enabled, command, () => ticks, ImmediateScheduler.Instance);

        ticks.OnNext(0);
        count.Should().Be(0);

        enabled.OnNext(true);
        ticks.OnNext(1);
        count.Should().Be(1);

        enabled.OnNext(false);
        ticks.OnNext(2);
        count.Should().Be(1);

        enabled.OnNext(true);
        ticks.OnNext(3);
        count.Should().Be(2);
    }

    [Fact]
    public async Task Start_drops_ticks_while_previous_refresh_is_running()
    {
        using var enabled = new BehaviorSubject<bool>(true);
        using var ticks = new Subject<long>();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleAfterRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawRunning = false;
        var count = 0;
        var command = ReactiveCommand.CreateFromTask(async () =>
        {
            count++;
            refreshStarted.TrySetResult();
            await releaseRefresh.Task;
            refreshCompleted.TrySetResult();
        });
        using var executingSubscription = command.IsExecuting.Subscribe(isExecuting =>
        {
            if (isExecuting)
                sawRunning = true;
            else if (sawRunning)
                idleAfterRefresh.TrySetResult();
        });

        using var subscription = AutoRefresh.Start(enabled, command, () => ticks, ImmediateScheduler.Instance);

        ticks.OnNext(1);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        ticks.OnNext(2);
        count.Should().Be(1);

        releaseRefresh.SetResult();
        await refreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await idleAfterRefresh.Task.WaitAsync(TimeSpan.FromSeconds(1));

        ticks.OnNext(3);
        count.Should().Be(2);
    }
}
