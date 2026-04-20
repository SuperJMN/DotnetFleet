using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DotnetFleet.ViewModels;

namespace DotnetFleet.Views;

public partial class JobDetailView : UserControl
{
    public JobDetailView()
    {
        AvaloniaXamlLoader.Load(this);

        // Auto-scroll to bottom when logs are added
        DataContextChanged += (_, _) =>
        {
            if (DataContext is JobDetailViewModel vm)
            {
                vm.Logs.CollectionChanged += (_, _) =>
                {
                    var scroll = this.FindDescendantOfType<ScrollViewer>();
                    scroll?.ScrollToEnd();
                };
            }
        };
    }
}
