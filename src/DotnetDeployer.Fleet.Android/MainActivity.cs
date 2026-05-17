using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace DotnetDeployer.Fleet.Android;

[Activity(
    Label = "DotnetDeployer.Fleet",
    Icon = "@mipmap/icon",
    Theme = "@style/MyTheme.Splash",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation
                         | ConfigChanges.ScreenSize
                         | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
