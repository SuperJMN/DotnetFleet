using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace DotnetFleet.Android;

[Activity(
    Label = "DotnetFleet",
    Icon = "@mipmap/icon",
    Theme = "@style/MyTheme.Splash",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation
                         | ConfigChanges.ScreenSize
                         | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
