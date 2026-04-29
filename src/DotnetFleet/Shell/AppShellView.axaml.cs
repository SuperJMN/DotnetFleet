using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace DotnetFleet.Shell;

public partial class AppShellView : UserControl
{
    public AppShellView()
    {
        AvaloniaXamlLoader.Load(this);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (DataContext is AppShellViewModel vm)
                {
                    vm.EnsureInitialSection();
                }
            },
            DispatcherPriority.Background);
    }
}
