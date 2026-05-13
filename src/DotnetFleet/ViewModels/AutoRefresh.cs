using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using ReactiveUI;

namespace DotnetFleet.ViewModels;

internal static class AutoRefreshIntervals
{
    public static readonly TimeSpan Section = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Detail = TimeSpan.FromSeconds(2);
}

internal static class AutoRefresh
{
    public static IDisposable Start(
        IObservable<bool> enabled,
        ReactiveCommand<Unit, Unit> command,
        TimeSpan interval) =>
        Start(
            enabled,
            command,
            () => Observable.Timer(TimeSpan.Zero, interval, TaskPoolScheduler.Default),
            RxSchedulers.MainThreadScheduler);

    internal static IDisposable Start(
        IObservable<bool> enabled,
        ReactiveCommand<Unit, Unit> command,
        Func<IObservable<long>> ticks,
        IScheduler dispatchScheduler)
    {
        return enabled
            .DistinctUntilChanged()
            .Select(isEnabled => isEnabled ? ticks() : Observable.Empty<long>())
            .Switch()
            .WithLatestFrom(command.IsExecuting.StartWith(false), (_, isExecuting) => isExecuting)
            .Where(isExecuting => !isExecuting)
            .ObserveOn(dispatchScheduler)
            .Subscribe(_ => command.Execute(Unit.Default).Subscribe(_ => { }, _ => { }), _ => { });
    }
}
