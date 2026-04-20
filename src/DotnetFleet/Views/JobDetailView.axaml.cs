using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DotnetFleet.ViewModels;

namespace DotnetFleet.Views;

public partial class JobDetailView : UserControl
{
    private const double AutoScrollEpsilon = 8.0;
    private NotifyCollectionChangedEventHandler? _logsHandler;
    private JobDetailViewModel? _attachedVm;

    public JobDetailView()
    {
        AvaloniaXamlLoader.Load(this);

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attachedVm is not null && _logsHandler is not null)
        {
            ((INotifyCollectionChanged)_attachedVm.FilteredLogs).CollectionChanged -= _logsHandler;
            _attachedVm = null;
            _logsHandler = null;
        }

        if (DataContext is not JobDetailViewModel vm) return;

        _attachedVm = vm;
        _logsHandler = (_, _) => Dispatcher.UIThread.Post(MaybeAutoScroll);
        ((INotifyCollectionChanged)vm.FilteredLogs).CollectionChanged += _logsHandler;
    }

    private void MaybeAutoScroll()
    {
        var scroll = this.FindControl<ScrollViewer>("LogScroll");
        if (scroll is null) return;

        var distanceFromBottom = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
        if (distanceFromBottom <= AutoScrollEpsilon)
            scroll.ScrollToEnd();
    }
}
