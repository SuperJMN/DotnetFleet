using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using ReactiveUI.Avalonia;
using FleetApp = DotnetDeployer.Fleet.App.App;

[assembly: SupportedOSPlatform("browser")]

namespace DotnetDeployer.Fleet.Browser;

internal sealed partial class Program
{
    private static async Task Main(string[] args)
    {
        await BuildAvaloniaApp()
            .WithInterFont()
            .UseReactiveUI(_ => { })
            .StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<FleetApp>();
}
