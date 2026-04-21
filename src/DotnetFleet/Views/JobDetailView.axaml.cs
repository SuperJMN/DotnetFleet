using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaTerminal;
using DotnetFleet.Core.Logging;
using DotnetFleet.ViewModels;
using DotnetFleet.Views.Logging;

namespace DotnetFleet.Views;

public partial class JobDetailView : UserControl
{
    private NotifyCollectionChangedEventHandler? _logsHandler;
    private JobDetailViewModel? _attachedVm;

    public JobDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachFromVm();

        if (DataContext is not JobDetailViewModel vm) return;

        _attachedVm = vm;

        var terminal = LogTerminal;
        if (terminal is null) return;

        terminal.Model ??= new TerminalControlModel();
        vm.SetTerminalModel(terminal.Model);

        // Feed existing lines
        FeedLines(vm.TerminalModel, vm.FilteredLogs);

        _logsHandler = (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var model = vm.TerminalModel;
                    if (model is null) return;

                    foreach (LogLine line in args.NewItems)
                        model.Feed(LogAnsi.Format(line) + "\r\n");
                });
            }
            else
            {
                // Reset/filter change — rebuild terminal content
                Dispatcher.UIThread.Post(() => RebuildTerminal(vm));
            }
        };
        ((INotifyCollectionChanged)vm.FilteredLogs).CollectionChanged += _logsHandler;
    }

    private void RebuildTerminal(JobDetailViewModel vm)
    {
        var model = vm.TerminalModel;
        if (model is null) return;

        // Clear and re-feed: reset terminal with escape code
        model.Feed("\x1b[2J\x1b[H");
        FeedLines(model, vm.FilteredLogs);
    }

    private static void FeedLines(TerminalControlModel? model, IEnumerable<LogLine> lines)
    {
        if (model is null) return;
        foreach (var line in lines)
            model.Feed(LogAnsi.Format(line) + "\r\n");
    }

    private void DetachFromVm()
    {
        if (_attachedVm is not null && _logsHandler is not null)
            ((INotifyCollectionChanged)_attachedVm.FilteredLogs).CollectionChanged -= _logsHandler;

        _attachedVm = null;
        _logsHandler = null;
    }
}
