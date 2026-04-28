using System.Globalization;
using System.Text.RegularExpressions;
using DotnetFleet.Core.Domain;

namespace DotnetFleet.WorkerService.Execution;

/// <summary>
/// Parses lines emitted by DotnetDeployer in the form
/// <c>##deployer[phase.start name=&lt;id&gt; ...attrs]</c>,
/// <c>##deployer[phase.end name=&lt;id&gt; status=&lt;ok|fail&gt; duration_ms=&lt;n&gt;]</c>,
/// <c>##deployer[phase.info name=&lt;id&gt; message="..."]</c>.
///
/// Returns <c>null</c> for any line that is not a recognized marker.
/// </summary>
public static class PhaseMarkerParser
{
    private static readonly Regex MarkerRegex = new(
        @"^##deployer\[phase\.(?<kind>start|end|info)\s+(?<body>[^\]]*)\]\s*$",
        RegexOptions.Compiled);

    public static PhaseEvent? TryParse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = MarkerRegex.Match(line);
        if (!m.Success) return null;

        var kind = m.Groups["kind"].Value switch
        {
            "start" => PhaseEventKind.Start,
            "end" => PhaseEventKind.End,
            "info" => PhaseEventKind.Info,
            _ => PhaseEventKind.Info
        };

        var attrs = ParseAttrs(m.Groups["body"].Value);
        if (!attrs.TryGetValue("name", out var name) || string.IsNullOrEmpty(name))
            return null;

        attrs.Remove("name");

        var ev = new PhaseEvent
        {
            Kind = kind,
            Name = name,
            Attrs = attrs
        };

        if (attrs.TryGetValue("status", out var status))
        {
            ev.Status = status.Equals("ok", StringComparison.OrdinalIgnoreCase) ? PhaseStatus.Ok
                      : status.Equals("fail", StringComparison.OrdinalIgnoreCase) ? PhaseStatus.Fail
                      : PhaseStatus.Unknown;
            attrs.Remove("status");
        }

        if (attrs.TryGetValue("duration_ms", out var dms) &&
            long.TryParse(dms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dmsValue))
        {
            ev.DurationMs = dmsValue;
            attrs.Remove("duration_ms");
        }

        if (attrs.TryGetValue("message", out var msg))
        {
            ev.Message = msg;
            attrs.Remove("message");
        }

        return ev;
    }

    /// <summary>
    /// Parses a body of the form <c>k=v k="quoted v" k2=v2</c>. Quoted values
    /// may contain <c>\"</c> and <c>\\</c> escapes. Returns a dictionary using
    /// last-write-wins for repeated keys.
    /// </summary>
    private static Dictionary<string, string> ParseAttrs(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < body.Length)
        {
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            if (i >= body.Length) break;

            var keyStart = i;
            while (i < body.Length && body[i] != '=' && !char.IsWhiteSpace(body[i])) i++;
            if (i >= body.Length || body[i] != '=') break;
            var key = body.Substring(keyStart, i - keyStart);
            i++; // skip '='

            string value;
            if (i < body.Length && body[i] == '"')
            {
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < body.Length && body[i] != '"')
                {
                    if (body[i] == '\\' && i + 1 < body.Length)
                    {
                        sb.Append(body[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(body[i]);
                        i++;
                    }
                }
                if (i < body.Length) i++; // closing quote
                value = sb.ToString();
            }
            else
            {
                var valStart = i;
                while (i < body.Length && !char.IsWhiteSpace(body[i])) i++;
                value = body.Substring(valStart, i - valStart);
            }

            if (!string.IsNullOrEmpty(key))
                result[key] = value;
        }
        return result;
    }
}
