using DotnetFleet.Coordinator.Services;

namespace DotnetFleet.Tests;

public class GitVersionLineParserTests
{
    [Theory]
    [InlineData("FullSemVer: 1.2.3", "1.2.3")]
    [InlineData("InformationalVersion: 2.4.1-beta.5+abcdef", "2.4.1-beta.5+abcdef")]
    [InlineData("SemVer: 0.0.31", "0.0.31")]
    [InlineData("MajorMinorPatch: 10.20.30", "10.20.30")]
    [InlineData("AssemblyInformationalVersion: 1.0.0-alpha", "1.0.0-alpha")]
    [InlineData("NuGetVersionV2: 1.2.3-rc.1", "1.2.3-rc.1")]
    [InlineData("\"FullSemVer\": \"1.2.3-beta.4+5\",", "1.2.3-beta.4+5")]
    [InlineData("Version: 1.2.3", "1.2.3")]
    public void Recognises_typical_GitVersion_keys(string line, string expected)
    {
        GitVersionLineParser.TryExtract(line).Should().Be(expected);
    }

    [Theory]
    [InlineData("\u001b[32mFullSemVer: 1.2.3\u001b[0m", "1.2.3")]
    [InlineData("\u001b[1;36mInformationalVersion:\u001b[0m \u001b[33m9.8.7-rc.1\u001b[0m", "9.8.7-rc.1")]
    public void Strips_ANSI_colour_codes_before_matching(string line, string expected)
    {
        GitVersionLineParser.TryExtract(line).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Just a regular log line, nothing to see here.")]
    [InlineData("error: build failed at step 3")]
    [InlineData("Version is unknown")]
    [InlineData("FullSemVer:")]                       // missing value
    [InlineData("FullSemVer: not.a.version")]         // not numeric
    [InlineData("FullSemVer: 1.2")]                   // too short
    [InlineData("Some other thing: 1.2.3")]           // unknown key
    public void Returns_null_for_lines_without_a_recognisable_version(string? line)
    {
        GitVersionLineParser.TryExtract(line).Should().BeNull();
    }

    [Fact]
    public void Picks_first_recognised_key_within_a_line()
    {
        // If the same line happens to mention multiple keys, the parser still extracts a
        // version — the first matching one is fine for naming purposes.
        var line = "[INFO] FullSemVer: 1.2.3 (computed from MajorMinorPatch: 1.2.3)";
        GitVersionLineParser.TryExtract(line).Should().Be("1.2.3");
    }

    [Fact]
    public void Tolerates_leading_and_trailing_garbage()
    {
        var line = "  [11:42:07 INF] >> FullSemVer: 4.5.6-preview.1+build.42  ";
        GitVersionLineParser.TryExtract(line).Should().Be("4.5.6-preview.1+build.42");
    }
}
