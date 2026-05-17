using System;
using Avalonia;
using ReactiveUI.Avalonia;
using Zafiro.Avalonia.Mcp.AppHost;
using FleetApp = DotnetDeployer.Fleet.App.App;

namespace DotnetDeployer.Fleet.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<FleetApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseMcpDiagnostics()
#if DEBUG
            .WithDeveloperTools()
#endif
            .UseReactiveUI(_ => { });
}
