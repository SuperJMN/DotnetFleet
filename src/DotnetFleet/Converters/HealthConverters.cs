using Avalonia.Data.Converters;
using Avalonia.Media;
using DotnetFleet.ViewModels;

namespace DotnetFleet.Converters;

public static class HealthConverters
{
    public static readonly IValueConverter ToBrush = new FuncValueConverter<BackendHealth, IBrush>(state => state switch
    {
        BackendHealth.Healthy => new SolidColorBrush(Color.Parse("#3FB950")),
        BackendHealth.Checking => new SolidColorBrush(Color.Parse("#F0B72F")),
        BackendHealth.Unreachable => new SolidColorBrush(Color.Parse("#F85149")),
        BackendHealth.Error => new SolidColorBrush(Color.Parse("#F85149")),
        _ => new SolidColorBrush(Color.Parse("#8B949E"))
    });

    public static readonly IValueConverter ToLabel = new FuncValueConverter<BackendHealth, string>(state => state switch
    {
        BackendHealth.Healthy => "Connected",
        BackendHealth.Checking => "Checking…",
        BackendHealth.Unreachable => "No connectivity",
        BackendHealth.Error => "Error",
        _ => "Unknown"
    });
}
