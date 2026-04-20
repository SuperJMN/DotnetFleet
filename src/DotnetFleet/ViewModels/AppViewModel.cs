using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class AppViewModel : ReactiveObject, IDisposable
{
    private readonly FleetApiClient _client;
    private readonly ISettingsService _settings;
    private readonly IBackendHealthMonitor _health;
    private readonly CompositeDisposable _subscriptions = new();

    [Reactive] private object? _currentPage;
    [Reactive] private BackendHealth _backendHealth = BackendHealth.Unknown;
    [Reactive] private string? _backendHealthMessage;
    [Reactive] private string? _backendVersion;
    [Reactive] private DateTimeOffset? _backendLastChecked;
    [Reactive] private string? _backendEndpoint;

    public AppViewModel(FleetApiClient client, ISettingsService settings, IBackendHealthMonitor health)
    {
        _client = client;
        _settings = settings;
        _health = health;

        var savedEndpoint = settings.GetEndpoint();
        var savedToken = settings.GetToken();

        if (savedEndpoint is not null && savedToken is not null)
        {
            _client.SetBaseAddress(savedEndpoint);
            _client.SetToken(savedToken);
            NavigateToMain();
        }
        else
        {
            NavigateToConnect();
        }

        ReconnectCommand = ReactiveCommand.Create(NavigateToConnect);

        _subscriptions.Add(_health.Snapshots
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplySnapshot));

        // Server rejected our token — drop it and bounce back to login so the user
        // can re-authenticate and pick up a freshly-signed JWT.
        _subscriptions.Add(_client.Unauthorized
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => HandleUnauthorized()));
    }

    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }

    public void NavigateToConnect()
    {
        var vm = new ConnectViewModel(_client, _settings);
        vm.Connected.Subscribe(_ =>
        {
            // If we already have a token (e.g. reconnecting from status bar), skip login.
            if (_client.IsAuthenticated && _settings.GetToken() is not null)
                NavigateToMain();
            else
                NavigateToLogin();
        });
        CurrentPage = vm;
    }

    public void NavigateToLogin()
    {
        var vm = new LoginViewModel(_client, _settings);
        vm.LoggedIn.Subscribe(_ => NavigateToMain());
        CurrentPage = vm;
    }

    public void NavigateToMain()
    {
        var vm = new MainViewModel(_client, _settings, this);
        CurrentPage = vm;
    }

    private void HandleUnauthorized()
    {
        if (CurrentPage is LoginViewModel)
        {
            return;
        }

        _settings.SetToken(null);
        _client.ClearToken();
        NavigateToLogin();
    }

    private void ApplySnapshot(BackendHealthSnapshot snap)
    {
        BackendHealth = snap.State;
        BackendHealthMessage = snap.Message;
        if (snap.Version is not null) BackendVersion = snap.Version;
        BackendLastChecked = snap.CheckedAt;
        BackendEndpoint = snap.Endpoint?.ToString();
    }

    public void Dispose() => _subscriptions.Dispose();
}
