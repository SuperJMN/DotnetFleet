using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using DotnetFleet.Core.Logging;
using DotnetFleet.ViewModels;
using DotnetFleet.Views.Logging;

namespace DotnetFleet.Views;

public partial class JobDetailView : UserControl
{
    private const double AutoScrollEpsilon = 8.0;
    private NotifyCollectionChangedEventHandler? _logsHandler;
    private JobDetailViewModel? _attachedVm;
    private LogSeverityColorizer? _colorizer;
    private readonly List<LogSeverity> _severities = new();

    public JobDetailView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
    }

    private TextEditor? Editor => this.FindControl<TextEditor>("LogEditor");

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachFromVm();

        if (DataContext is not JobDetailViewModel vm) return;

        _attachedVm = vm;

        var editor = Editor;
        if (editor is null) return;

        _severities.Clear();
        _colorizer = new LogSeverityColorizer(_severities);
        editor.TextArea.TextView.LineTransformers.Add(_colorizer);

        RebuildDocument(editor, vm);

        _logsHandler = (_, args) => Dispatcher.UIThread.Post(() => OnLogsChanged(editor, vm, args));
        ((INotifyCollectionChanged)vm.FilteredLogs).CollectionChanged += _logsHandler;
    }

    private void OnLogsChanged(TextEditor editor, JobDetailViewModel vm, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
        {
            AppendLines(editor, args.NewItems.Cast<LogLine>());
            MaybeAutoScroll(editor);
        }
        else
        {
            RebuildDocument(editor, vm);
        }
    }

    private void AppendLines(TextEditor editor, IEnumerable<LogLine> newLines)
    {
        var doc = editor.Document;
        doc.BeginUpdate();
        try
        {
            foreach (var line in newLines)
            {
                _severities.Add(line.Severity);
                var text = doc.TextLength == 0 ? line.Text : "\n" + line.Text;
                doc.Insert(doc.TextLength, text);
            }
        }
        finally
        {
            doc.EndUpdate();
        }

        editor.TextArea.TextView.Redraw();
    }

    private void RebuildDocument(TextEditor editor, JobDetailViewModel vm)
    {
        _severities.Clear();
        var lines = vm.FilteredLogs;
        foreach (var line in lines)
            _severities.Add(line.Severity);

        editor.Document.Text = string.Join("\n", lines.Select(l => l.Text));
        editor.TextArea.TextView.Redraw();
    }

    private static void MaybeAutoScroll(TextEditor editor)
    {
        var scrollViewer = editor.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer is null) return;

        var distanceFromBottom = scrollViewer.Extent.Height - scrollViewer.Viewport.Height - scrollViewer.Offset.Y;
        if (distanceFromBottom <= AutoScrollEpsilon)
            editor.ScrollToEnd();
    }

    private void DetachFromVm()
    {
        if (_attachedVm is not null && _logsHandler is not null)
            ((INotifyCollectionChanged)_attachedVm.FilteredLogs).CollectionChanged -= _logsHandler;

        var editor = Editor;
        if (editor is not null && _colorizer is not null)
            editor.TextArea.TextView.LineTransformers.Remove(_colorizer);

        _attachedVm = null;
        _logsHandler = null;
        _colorizer = null;
    }
}
