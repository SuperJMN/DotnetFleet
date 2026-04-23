using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace DotnetFleet.Android;

[Activity(
    Label = "DotnetFleet",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation
                         | ConfigChanges.ScreenSize
                         | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
