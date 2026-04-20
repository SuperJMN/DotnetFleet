using Avalonia.Markup.Xaml;

namespace DotnetFleet.Views;

public partial class MainView : Avalonia.Controls.UserControl
{
    public MainView() => AvaloniaXamlLoader.Load(this);
}
