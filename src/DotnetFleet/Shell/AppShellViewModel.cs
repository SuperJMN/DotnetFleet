using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DotnetFleet.ViewModels;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI.Shell;

namespace DotnetFleet.Shell;

public partial class AppShellViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];

    [Reactive] private BackendHealth _backendHealth;
    [Reactive] private string? _backendHealthMessage;
    [Reactive] private string? _backendVersion;
    [Reactive] private string? _backendEndpoint;

    public AppShellViewModel(IShell shell, IBackendHealthMonitor health, AppBootstrapper bootstrapper)
    {
        Shell = shell;

        var current = health.Current;
        _backendHealth = current.State;
        _backendHealthMessage = current.Message;
        _backendVersion = current.Version;
        _backendEndpoint = current.Endpoint?.ToString().TrimEnd('/');

        var sub = health.Snapshots
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(s =>
            {
                BackendHealth = s.State;
                BackendHealthMessage = s.Message;
                BackendVersion = s.Version;
                BackendEndpoint = s.Endpoint?.ToString().TrimEnd('/');
            });
        _disposables.Add(sub);

        ReconnectCommand = ReactiveCommand.CreateFromTask(bootstrapper.ShowConnectAsync);
        LogoutCommand = ReactiveCommand.CreateFromTask(bootstrapper.LogoutAsync);
    }

    public IShell Shell { get; }
    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }

    public void Dispose() => _disposables.Dispose();
}
