using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class AppViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly ISettingsService _settings;

    [Reactive] private object? _currentPage;

    public AppViewModel(FleetApiClient client, ISettingsService settings)
    {
        _client = client;
        _settings = settings;

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
    }

    public void NavigateToConnect()
    {
        var vm = new ConnectViewModel(_client, _settings);
        vm.Connected.Subscribe(_ => NavigateToLogin());
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
}
