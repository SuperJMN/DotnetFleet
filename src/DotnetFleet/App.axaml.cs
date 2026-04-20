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

        var httpClient = new HttpClient();
        var fleetClient = new FleetApiClient(httpClient);
        services.AddSingleton(fleetClient);
        services.AddSingleton<ISettingsService>(
            OperatingSystem.IsBrowser() ? new InMemorySettingsService() : new FileSettingsService());

        services.AddSingleton<AppViewModel>();

        var provider = services.BuildServiceProvider();

        var appVm = provider.GetRequiredService<AppViewModel>();

        this.Connect(
            () => new Views.MainShell(),
            _ => appVm,
            () => new Window { Title = "DotnetFleet", Width = 1100, Height = 720 });

        base.OnFrameworkInitializationCompleted();
    }
}
