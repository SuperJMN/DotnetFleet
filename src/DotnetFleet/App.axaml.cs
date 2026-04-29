using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DotnetFleet.Api.Client;
using DotnetFleet.Dialogs;
using DotnetFleet.Shell;
using DotnetFleet.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Zafiro.Avalonia.Dialogs;
using Zafiro.Avalonia.Icons;
using Zafiro.Avalonia.Misc;
using Zafiro.Avalonia.Storage;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell;
using Zafiro.UI.Shell.Utils;

namespace DotnetFleet;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        IconControlProviderRegistry.Register(new OptrisIconControlProvider(), asDefault: true);

        var logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        var services = new ServiceCollection();

        var httpHandler = new UnauthorizedHandler();
        var streamingHandler = new UnauthorizedHandler();
        var unauthorizedSignal = httpHandler.Unauthorized.Merge(streamingHandler.Unauthorized);

        var fleetClient = new FleetApiClient(httpHandler, streamingHandler, unauthorizedSignal);
        services.AddSingleton(fleetClient);
        services.AddSingleton<ISettingsService>(
            OperatingSystem.IsBrowser() ? new InMemorySettingsService() : new FileSettingsService());
        services.AddSingleton<IBackendHealthMonitor, BackendHealthMonitor>();

        services.AddSingleton<IShell, Zafiro.UI.Shell.Shell>();
        services.AddScoped<INavigator>(sp =>
            new Navigator(
                sp,
                CSharpFunctionalExtensions.Maybe<Serilog.ILogger>.From(logger),
                ReactiveUI.RxSchedulers.MainThreadScheduler));
        services.AddAllSectionsFromAttributes(logger);

        services.AddSingleton(DialogService.Create());
        services.AddSingleton<IFileSystemPicker>(_ => new AvaloniaFileSystemPicker(() =>
        {
            var lifetime = Current?.ApplicationLifetime;
            return lifetime switch
            {
                IClassicDesktopStyleApplicationLifetime desktop when desktop.MainWindow is not null
                    => TopLevel.GetTopLevel(desktop.MainWindow)!.StorageProvider,
                ISingleViewApplicationLifetime singleView when singleView.MainView is not null
                    => TopLevel.GetTopLevel(singleView.MainView)!.StorageProvider,
                _ => throw new InvalidOperationException("No top-level available for file picker."),
            };
        }));
        services.AddTransient<ConnectDialogViewModel>();
        services.AddTransient<LoginDialogViewModel>();
        services.AddSingleton<AppBootstrapper>();
        services.AddSingleton<AppShellViewModel>();

        var provider = services.BuildServiceProvider();

        var shell = provider.GetRequiredService<IShell>();
        this.Connect(
            () => new AppShellView(),
            _ => provider.GetRequiredService<AppShellViewModel>(),
            () => new Window { Title = "DotnetFleet", Width = 1200, Height = 800 });

        var bootstrapper = provider.GetRequiredService<AppBootstrapper>();
        Dispatcher.UIThread.Post(async () => await bootstrapper.RunAsync(), DispatcherPriority.Background);

        base.OnFrameworkInitializationCompleted();
    }
}
