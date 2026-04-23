using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DotnetFleet.Shell;

public partial class AppShellView : UserControl
{
    public AppShellView() => AvaloniaXamlLoader.Load(this);
}
