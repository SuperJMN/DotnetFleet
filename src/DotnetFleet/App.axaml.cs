using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DotnetFleet.Api.Client;
using DotnetFleet.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Zafiro.Avalonia.Misc;

namespace DotnetFleet;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();

        var httpHandler = new UnauthorizedHandler();
        var streamingHandler = new UnauthorizedHandler();
        var unauthorizedSignal = httpHandler.Unauthorized.Merge(streamingHandler.Unauthorized);

        var fleetClient = new FleetApiClient(httpHandler, streamingHandler, unauthorizedSignal);
        services.AddSingleton(fleetClient);
        services.AddSingleton<ISettingsService>(
            OperatingSystem.IsBrowser() ? new InMemorySettingsService() : new FileSettingsService());
        services.AddSingleton<IBackendHealthMonitor, BackendHealthMonitor>();

        services.AddSingleton<AppViewModel>();

        var provider = services.BuildServiceProvider();

        var appVm = provider.GetRequiredService<AppViewModel>();
        provider.GetRequiredService<IBackendHealthMonitor>().Start();

        this.Connect(
            () => new Views.MainShell(),
            _ => appVm,
            () => new Window { Title = "DotnetFleet", Width = 1100, Height = 720 });

        base.OnFrameworkInitializationCompleted();
    }
}
