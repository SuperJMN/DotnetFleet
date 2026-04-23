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
        LogSeverity.Trace   => "\x1b[37m",          // light gray
        LogSeverity.Debug   => "\x1b[94m",          // bright blue
        LogSeverity.Info    => "\x1b[97m",          // bright white
        LogSeverity.Warning => "\x1b[93m",          // bright yellow/amber
        LogSeverity.Error   => "\x1b[1;91m",        // bold bright red
        LogSeverity.Fatal   => "\x1b[1;91m",        // bold bright red (intense)
        _                   => null
    };
}
