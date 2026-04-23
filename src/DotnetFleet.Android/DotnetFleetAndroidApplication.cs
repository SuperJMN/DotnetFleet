using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ReactiveUI.Avalonia;

namespace DotnetFleet.Android;

[Application]
public class DotnetFleetAndroidApplication : AvaloniaAndroidApplication<global::DotnetFleet.App>
{
    public DotnetFleetAndroidApplication(IntPtr handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .UseReactiveUI(_ => { })
            .WithInterFont()
            .LogToTrace();
    }
}
