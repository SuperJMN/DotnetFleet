namespace DotnetFleet.Core.Logging;

public sealed record LogLine(string Text, LogSeverity Severity)
{
    public static LogLine FromText(string text) => new(text, LogSeverityClassifier.Classify(text));
}
