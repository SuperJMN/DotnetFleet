using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using DotnetFleet.Core.Logging;

namespace DotnetFleet.Views.Logging;

/// <summary>
/// Attached behavior that projects an <see cref="IEnumerable{LogLine}"/> into the
/// <see cref="SelectableTextBlock.Inlines"/> of a single text block. Using one text
/// block (instead of one per line) restores multi-line text selection and lets us
/// colour each line by severity and highlight search matches per <see cref="Run"/>.
/// </summary>
public static class LogTextPresenter
{
    public static readonly AttachedProperty<IEnumerable?> LinesProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, IEnumerable?>("Lines", typeof(LogTextPresenter));

    public static readonly AttachedProperty<string> SearchTextProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, string>("SearchText", typeof(LogTextPresenter), string.Empty);

    public static readonly AttachedProperty<int> MatchCountProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, int>("MatchCount", typeof(LogTextPresenter));

    public static void SetLines(SelectableTextBlock element, IEnumerable? value)
        => element.SetValue(LinesProperty, value);
    public static IEnumerable? GetLines(SelectableTextBlock element)
        => element.GetValue(LinesProperty);

    public static void SetSearchText(SelectableTextBlock element, string value)
        => element.SetValue(SearchTextProperty, value);
    public static string GetSearchText(SelectableTextBlock element)
        => element.GetValue(SearchTextProperty);

    public static void SetMatchCount(SelectableTextBlock element, int value)
        => element.SetValue(MatchCountProperty, value);
    public static int GetMatchCount(SelectableTextBlock element)
        => element.GetValue(MatchCountProperty);

    private static readonly Dictionary<SelectableTextBlock, NotifyCollectionChangedEventHandler> Subscriptions = new();

    static LogTextPresenter()
    {
        LinesProperty.Changed.Subscribe(args =>
        {
            if (args.Sender is not SelectableTextBlock target) return;
            UnsubscribeOld(target, args.OldValue.GetValueOrDefault());
            SubscribeNew(target, args.NewValue.GetValueOrDefault());
            Rebuild(target);
        });

        SearchTextProperty.Changed.Subscribe(args =>
        {
            if (args.Sender is SelectableTextBlock target) Rebuild(target);
        });
    }

    private static void UnsubscribeOld(SelectableTextBlock target, IEnumerable? old)
    {
        if (old is INotifyCollectionChanged incc &&
            Subscriptions.Remove(target, out var handler))
        {
            incc.CollectionChanged -= handler;
        }
    }

    private static void SubscribeNew(SelectableTextBlock target, IEnumerable? next)
    {
        if (next is INotifyCollectionChanged incc)
        {
            void Handler(object? _, NotifyCollectionChangedEventArgs e)
            {
                Dispatcher.UIThread.Post(() => OnCollectionChanged(target, e));
            }
            Subscriptions[target] = Handler;
            incc.CollectionChanged += Handler;
        }
    }

    private static void OnCollectionChanged(SelectableTextBlock target, NotifyCollectionChangedEventArgs e)
    {
        // Optimisation for streaming: append-only adds avoid rebuilding the whole inline tree.
        if (e.Action == NotifyCollectionChangedAction.Add &&
            string.IsNullOrEmpty(GetSearchText(target)) &&
            e.NewItems is not null)
        {
            AppendLines(target, e.NewItems);
            return;
        }
        Rebuild(target);
    }

    private static void AppendLines(SelectableTextBlock target, IList newItems)
    {
        var inlines = target.Inlines ??= new InlineCollection();
        foreach (var item in newItems)
        {
            if (item is not LogLine line) continue;
            if (inlines.Count > 0) inlines.Add(new LineBreak());
            AppendLineRuns(inlines, line, searchText: string.Empty, matchCounter: null);
        }
    }

    private static void Rebuild(SelectableTextBlock target)
    {
        var inlines = target.Inlines ??= new InlineCollection();
        inlines.Clear();

        var lines = GetLines(target);
        var search = GetSearchText(target) ?? string.Empty;
        var matchCount = 0;
        var counter = string.IsNullOrEmpty(search) ? null : (Action)(() => matchCount++);

        if (lines is not null)
        {
            var first = true;
            foreach (var item in lines)
            {
                if (item is not LogLine line) continue;
                if (!first) inlines.Add(new LineBreak());
                first = false;
                AppendLineRuns(inlines, line, search, counter);
            }
        }

        SetMatchCount(target, matchCount);
    }

    private static void AppendLineRuns(InlineCollection inlines, LogLine line, string searchText, Action? matchCounter)
    {
        var foreground = SeverityColors.GetForeground(line.Severity);
        var weight = line.Severity is LogSeverity.Error or LogSeverity.Fatal
            ? FontWeight.SemiBold
            : FontWeight.Normal;

        if (string.IsNullOrEmpty(searchText))
        {
            inlines.Add(MakeRun(line.Text, foreground, weight, highlight: false));
            return;
        }

        var text = line.Text;
        var i = 0;
        while (i < text.Length)
        {
            var idx = text.IndexOf(searchText, i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                inlines.Add(MakeRun(text[i..], foreground, weight, highlight: false));
                break;
            }
            if (idx > i)
                inlines.Add(MakeRun(text[i..idx], foreground, weight, highlight: false));
            inlines.Add(MakeRun(text.Substring(idx, searchText.Length), foreground, weight, highlight: true));
            matchCounter?.Invoke();
            i = idx + searchText.Length;
        }
    }

    private static Run MakeRun(string text, IBrush foreground, FontWeight weight, bool highlight)
    {
        var run = new Run(text)
        {
            Foreground = foreground,
            FontWeight = weight
        };
        if (highlight)
            run.Background = SeverityColors.MatchHighlight;
        return run;
    }
}

internal static class SeverityColors
{
    // Theme-friendly explicit colours; chosen to be readable on both light and dark backgrounds.
    public static readonly IBrush Default = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    public static readonly IBrush Trace   = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    public static readonly IBrush Debug   = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    public static readonly IBrush Info    = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
    public static readonly IBrush Warning = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    public static readonly IBrush Error   = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    public static readonly IBrush Fatal   = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public static readonly IBrush MatchHighlight = new SolidColorBrush(Color.FromArgb(0x80, 0xFA, 0xCC, 0x15));

    public static IBrush GetForeground(LogSeverity severity) => severity switch
    {
        LogSeverity.Trace => Trace,
        LogSeverity.Debug => Debug,
        LogSeverity.Info => Info,
        LogSeverity.Warning => Warning,
        LogSeverity.Error => Error,
        LogSeverity.Fatal => Fatal,
        _ => Default
    };
}
