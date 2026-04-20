using System;
using Avalonia;
using DotnetFleet;
using ReactiveUI.Avalonia;
using Zafiro.Avalonia.Mcp.AppHost;

namespace DotnetFleet.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseMcpDiagnostics()
#if DEBUG
            .WithDeveloperTools()
#endif
            .UseReactiveUI(_ => { });
}
