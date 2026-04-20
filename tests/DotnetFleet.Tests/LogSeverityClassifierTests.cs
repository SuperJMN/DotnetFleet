using DotnetFleet.Core.Logging;

namespace DotnetFleet.Tests;

public class LogSeverityClassifierTests
{
    [Theory]
    [InlineData("ERROR: something blew up", LogSeverity.Error)]
    [InlineData("error: lower case", LogSeverity.Error)]
    [InlineData("[ERR] short form", LogSeverity.Error)]
    [InlineData("Build FAILED.", LogSeverity.Error)]
    [InlineData("System.NullReferenceException at ...", LogSeverity.Error)]
    [InlineData("Operation FAILURE detected", LogSeverity.Error)]
    public void Classifies_errors(string line, LogSeverity expected)
        => LogSeverityClassifier.Classify(line).Should().Be(expected);

    [Theory]
    [InlineData("WARN: deprecated API", LogSeverity.Warning)]
    [InlineData("warning C4996: ...", LogSeverity.Warning)]
    [InlineData("[WRN] short form", LogSeverity.Warning)]
    public void Classifies_warnings(string line, LogSeverity expected)
        => LogSeverityClassifier.Classify(line).Should().Be(expected);

    [Theory]
    [InlineData("FATAL: out of memory", LogSeverity.Fatal)]
    [InlineData("CRITICAL failure in subsystem", LogSeverity.Fatal)]
    public void Classifies_fatal(string line, LogSeverity expected)
        => LogSeverityClassifier.Classify(line).Should().Be(expected);

    [Theory]
    [InlineData("INFO: server started", LogSeverity.Info)]
    [InlineData("[INF] listening on :5000", LogSeverity.Info)]
    public void Classifies_info(string line, LogSeverity expected)
        => LogSeverityClassifier.Classify(line).Should().Be(expected);

    [Theory]
    [InlineData("DEBUG: payload received", LogSeverity.Debug)]
    [InlineData("[DBG] x=42", LogSeverity.Debug)]
    public void Classifies_debug(string line, LogSeverity expected)
        => LogSeverityClassifier.Classify(line).Should().Be(expected);

    [Theory]
    [InlineData("TRACE entered method", LogSeverity.Trace)]
    [InlineData("VERBOSE detail", LogSeverity.Trace)]
    public void Classifies_trace(string line, LogSeverity expected)
        => LogSeverityClassifier.Classify(line).Should().Be(expected);

    [Theory]
    [InlineData("This is an erroneous claim")]
    [InlineData("Just some information here")]
    [InlineData("Performance characteristics")]
    [InlineData("inferno burning bright")]
    [InlineData("debugger attached")]
    [InlineData("warned previously")]
    public void Does_not_match_partial_words(string line)
        => LogSeverityClassifier.Classify(line).Should().Be(LogSeverity.None);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_or_whitespace_is_None(string? line)
        => LogSeverityClassifier.Classify(line).Should().Be(LogSeverity.None);

    [Fact]
    public void Fatal_takes_precedence_over_error()
        => LogSeverityClassifier.Classify("FATAL ERROR: kernel panic").Should().Be(LogSeverity.Fatal);

    [Fact]
    public void Error_takes_precedence_over_warning()
        => LogSeverityClassifier.Classify("WARN: previous step had ERROR")
            .Should().Be(LogSeverity.Error);

    [Fact]
    public void Detects_keyword_after_timestamp_prefix()
        => LogSeverityClassifier.Classify("2025-04-20T22:19:25Z [ERROR] boom")
            .Should().Be(LogSeverity.Error);
}
