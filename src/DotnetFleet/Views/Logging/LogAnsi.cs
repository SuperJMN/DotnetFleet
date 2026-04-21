using DotnetFleet.Core.Logging;

namespace DotnetFleet.Views.Logging;

/// <summary>
/// Converts <see cref="LogLine"/> to ANSI-colored terminal text.
/// </summary>
internal static class LogAnsi
{
    private const string Reset = "\x1b[0m";

    public static string Format(LogLine line)
    {
        var code = GetAnsiCode(line.Severity);
        return code is null ? line.Text : $"{code}{line.Text}{Reset}";
    }

    private static string? GetAnsiCode(LogSeverity severity) => severity switch
    {
        LogSeverity.Trace   => "\x1b[90m",        // bright black (gray)
        LogSeverity.Debug   => "\x1b[37m",         // white (dim)
        LogSeverity.Info    => "\x1b[34m",          // blue
        LogSeverity.Warning => "\x1b[33m",          // yellow
        LogSeverity.Error   => "\x1b[1;31m",        // bold red
        LogSeverity.Fatal   => "\x1b[1;91m",        // bold bright red
        _                   => null
    };
}
