using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using DotnetFleet.Dialogs;
using DotnetFleet.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI.Commands;

namespace DotnetFleet;

public class AppBootstrapper
{
    private readonly IServiceProvider _services;
    private readonly FleetApiClient _client;
    private readonly ISettingsService _settings;
    private readonly IDialog _dialog;
    private readonly IBackendHealthMonitor _health;
    private bool _loginDialogOpen;

    public AppBootstrapper(IServiceProvider services, FleetApiClient client, ISettingsService settings,
        IDialog dialog, IBackendHealthMonitor health)
    {
        _services = services;
        _client = client;
        _settings = settings;
        _dialog = dialog;
        _health = health;

        _client.Unauthorized
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await ReauthenticateAsync());
    }

    public async Task RunAsync()
    {
        var endpoint = _settings.GetEndpoint();
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            _client.SetBaseAddress(endpoint);
        }

        var token = _settings.GetToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            _client.SetToken(token);
        }

        _health.Start();

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            if (!await ShowConnectDialogAsync()) return;
        }

        if (!_client.IsAuthenticated)
        {
            await ShowLoginDialogAsync();
        }
    }

    public async Task ShowConnectAsync() => await ShowConnectDialogAsync();

    public async Task LogoutAsync()
    {
        _settings.ClearToken();
        _client.ClearToken();
        await ShowLoginDialogAsync();
    }

    private Task<bool> ShowConnectDialogAsync()
    {
        var vm = _services.GetRequiredService<ConnectDialogViewModel>();
        return _dialog.Show<ConnectDialogViewModel>(vm, "Coordinator endpoint", (v, closeable) => new IOption[]
        {
            new Option("Cancel",
                ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                new Settings { IsCancel = true, Role = OptionRole.Cancel }),
            new Option("Connect",
                ReactiveCommand.CreateFromTask(async () =>
                {
                    if (await v.TryConnectAsync()) closeable.Close();
                }, v.CanConnect).Enhance(),
                new Settings { IsDefault = true, Role = OptionRole.Primary }),
        });
    }

    private async Task ShowLoginDialogAsync()
    {
        if (_loginDialogOpen) return;
        _loginDialogOpen = true;
        try
        {
            var vm = _services.GetRequiredService<LoginDialogViewModel>();
            await _dialog.Show<LoginDialogViewModel>(vm, "Sign in", (v, closeable) => new IOption[]
            {
                new Option("Cancel",
                    ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                    new Settings { IsCancel = true, Role = OptionRole.Cancel }),
                new Option("Sign in",
                    ReactiveCommand.CreateFromTask(async () =>
                    {
                        if (await v.TryLoginAsync()) closeable.Close();
                    }, v.CanLogin).Enhance(),
                    new Settings { IsDefault = true, Role = OptionRole.Primary }),
            });
        }
        finally
        {
            _loginDialogOpen = false;
        }
    }

    private Task ReauthenticateAsync()
    {
        _client.ClearToken();
        _settings.ClearToken();
        return ShowLoginDialogAsync();
    }
}
