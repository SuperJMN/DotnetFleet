using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using DotnetFleet.Core.Logging;

namespace DotnetFleet.Views.Logging;

/// <summary>
/// Colours each line of the <see cref="TextEditor"/> document according to its
/// <see cref="LogSeverity"/>. The severity list is maintained externally and kept
/// in sync with the document lines.
/// </summary>
public sealed class LogSeverityColorizer : DocumentColorizingTransformer
{
    private readonly IReadOnlyList<LogSeverity> _severities;

    public LogSeverityColorizer(IReadOnlyList<LogSeverity> severities)
    {
        _severities = severities;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var index = line.LineNumber - 1; // LineNumber is 1-based
        if (index < 0 || index >= _severities.Count) return;

        var severity = _severities[index];
        var foreground = SeverityBrushes.GetForeground(severity);
        var weight = severity is LogSeverity.Error or LogSeverity.Fatal
            ? FontWeight.SemiBold
            : FontWeight.Normal;

        ChangeLinePart(line.Offset, line.EndOffset, element =>
        {
            element.TextRunProperties.SetForegroundBrush(foreground);
            if (weight != FontWeight.Normal)
            {
                var typeface = element.TextRunProperties.Typeface;
                element.TextRunProperties.SetTypeface(new Typeface(typeface.FontFamily, typeface.Style, weight));
            }
        });
    }
}

internal static class SeverityBrushes
{
    public static readonly IBrush Default = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    public static readonly IBrush Trace   = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    public static readonly IBrush Debug   = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    public static readonly IBrush Info    = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
    public static readonly IBrush Warning = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    public static readonly IBrush Error   = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    public static readonly IBrush Fatal   = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public static IBrush GetForeground(LogSeverity severity) => severity switch
    {
        LogSeverity.Trace   => Trace,
        LogSeverity.Debug   => Debug,
        LogSeverity.Info    => Info,
        LogSeverity.Warning => Warning,
        LogSeverity.Error   => Error,
        LogSeverity.Fatal   => Fatal,
        _                   => Default
    };
}
