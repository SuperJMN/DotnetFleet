using System.Text.RegularExpressions;

namespace DotnetFleet.Core.Logging;

public static class LogSeverityClassifier
{
    private static readonly Regex FatalRegex = new(@"\b(FATAL|CRITICAL|PANIC)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Negative lookbehind excludes "0 Error(s)" / "0 errors" (MSBuild summaries).
    // Negative lookahead excludes "Failed: 0" / "Errors = 0" (test summaries).
    private static readonly Regex ErrorRegex = new(
        @"(?<!\b0\s+)(\b(ERROR|ERRORS|ERR|FAIL|FAILED|FAILURE|EXCEPTION)\b|\b\w*Exception\b)(?!\s*[:=]\s*0\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WarningRegex = new(
        @"(?<!\b0\s+)\b(WARN|WARNING|WARNINGS|WRN)\b(?!\s*[:=]\s*0\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InfoRegex = new(@"\b(INFO|INF)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DebugRegex = new(@"\b(DEBUG|DBG)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TraceRegex = new(@"\b(TRACE|TRC|VERBOSE|VRB)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static LogSeverity Classify(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return LogSeverity.None;

        if (FatalRegex.IsMatch(line)) return LogSeverity.Fatal;
        if (ErrorRegex.IsMatch(line)) return LogSeverity.Error;
        if (WarningRegex.IsMatch(line)) return LogSeverity.Warning;
        if (InfoRegex.IsMatch(line)) return LogSeverity.Info;
        if (DebugRegex.IsMatch(line)) return LogSeverity.Debug;
        if (TraceRegex.IsMatch(line)) return LogSeverity.Trace;

        return LogSeverity.None;
    }
}
