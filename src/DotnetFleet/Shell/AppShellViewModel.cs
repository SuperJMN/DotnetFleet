using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetFleet.ViewModels;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI.Commands;
using Zafiro.UI.Navigation.Sections;
using Zafiro.UI.Shell;

namespace DotnetFleet.Shell;

public partial class AppShellViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];
    private readonly SerialDisposable _navigatorContentSubscription = new();

    [Reactive] private BackendHealth _backendHealth;
    [Reactive] private string? _backendHealthMessage;
    [Reactive] private string? _backendVersion;
    [Reactive] private string? _backendEndpoint;
    [Reactive] private ISection? _selectedSection;
    [Reactive] private object? _currentContent;
    [Reactive] private IEnhancedCommand<Result>? _backCommand;

    public AppShellViewModel(IShell shell, IBackendHealthMonitor health, AppBootstrapper bootstrapper)
    {
        Shell = shell;

        var current = health.Current;
        _backendHealth = current.State;
        _backendHealthMessage = current.Message;
        _backendVersion = current.Version;
        _backendEndpoint = current.Endpoint?.ToString().TrimEnd('/');

        health.Snapshots
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(s =>
            {
                BackendHealth = s.State;
                BackendHealthMessage = s.Message;
                BackendVersion = s.Version;
                BackendEndpoint = s.Endpoint?.ToString().TrimEnd('/');
            })
            .DisposeWithCollection(_disposables);

        ReconnectCommand = ReactiveCommand.CreateFromTask(bootstrapper.ShowConnectAsync);
        LogoutCommand = ReactiveCommand.CreateFromTask(bootstrapper.LogoutAsync);

        _disposables.Add(_navigatorContentSubscription);

        Shell.SelectedSection
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(section => SelectedSection = section)
            .DisposeWithCollection(_disposables);

        this.WhenAnyValue(x => x.SelectedSection)
            .WhereNotNull()
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplySelectedSection)
            .DisposeWithCollection(_disposables);

        SelectedSection = Shell.SelectedSection.Value;
    }

    public IShell Shell { get; }
    public IEnumerable<ISection> Sections => Shell.Sections;
    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }

    private void ApplySelectedSection(ISection section)
    {
        if (!ReferenceEquals(Shell.SelectedSection.Value, section))
        {
            Shell.SelectedSection.Value = section;
        }

        BackCommand = section.Navigator.Back;
        CurrentContent = null;
        _navigatorContentSubscription.Disposable = section.Navigator.Content
            .Subscribe(content => CurrentContent = content);
    }

    public void Dispose() => _disposables.Dispose();
}

file static class DisposableExtensions
{
    public static void DisposeWithCollection(this IDisposable disposable, CompositeDisposable disposables)
    {
        disposables.Add(disposable);
    }
}
