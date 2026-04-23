using System.Text.RegularExpressions;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// Pure, side-effect-free scanner that recognises a deployment version inside a single
/// log line. Designed to handle output from GitVersion, Nerdbank.GitVersioning and MinVer
/// (and any other tool that prints a SemVer in <c>Key: value</c> form).
///
/// The parser strips ANSI colour escape sequences before matching so it works against
/// raw worker stdout. Only the version *value* is returned — never the original log line.
/// </summary>
public static class GitVersionLineParser
{
    private static readonly Regex AnsiEscape = new(
        "\x1B\\[[0-9;?]*[A-Za-z]",
        RegexOptions.Compiled);

    // SemVer-ish payload: 1.2.3, optionally followed by -prerelease and/or +metadata.
    private const string SemVerCore =
        @"(?<ver>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z][0-9A-Za-z.\-+]*)?)";

    // Keys are ordered by *informational richness*. The first match wins for a given line,
    // so when GitVersion prints both "MajorMinorPatch" and "FullSemVer" on different lines
    // we pick whichever comes first in the stream — but the integration code only writes
    // the first detection per job anyway.
    private static readonly Regex KeyedVersion = new(
        @"\b(?<key>FullSemVer|InformationalVersion|AssemblyInformationalVersion|SemVer|NuGetVersionV2|NuGetVersion|MajorMinorPatch|AssemblySemVer|Version)\b""?\s*[:=]\s*""?" + SemVerCore + "\"?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Tries to extract a version string from <paramref name="line"/>. Returns null when
    /// the line carries no recognisable version, is empty, or is malformed.
    /// </summary>
    public static string? TryExtract(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var clean = AnsiEscape.Replace(line, string.Empty);
        var match = KeyedVersion.Match(clean);
        return match.Success ? match.Groups["ver"].Value : null;
    }
}
