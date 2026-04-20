using Avalonia.Markup.Xaml;

namespace DotnetFleet.Views;

public partial class MainShell : Avalonia.Controls.UserControl
{
    public MainShell() => AvaloniaXamlLoader.Load(this);
}
