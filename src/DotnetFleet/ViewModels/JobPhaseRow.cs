using DotnetFleet.Core.Domain;

namespace DotnetFleet.ViewModels;

/// <summary>
/// View-model row for a single <see cref="JobPhase"/>. Wraps the domain entity
/// with display-friendly properties (icon by status, formatted duration,
/// localised display name) so the AXAML stays declarative.
/// </summary>
public class JobPhaseRow
{
    public string Icon { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string DurationText { get; init; } = "";

    public static JobPhaseRow From(JobPhase phase, Func<string, string> formatName)
    {
        var icon = phase.EndedAt is null
            ? "⏳"
            : phase.Status switch
            {
                PhaseStatus.Ok => "✅",
                PhaseStatus.Fail => "❌",
                _ => "•"
            };

        string duration;
        if (phase.DurationMs is { } ms)
            duration = ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0}s";
        else if (phase.EndedAt is null)
            duration = "running…";
        else
            duration = "";

        return new JobPhaseRow
        {
            Icon = icon,
            DisplayName = formatName(phase.Name),
            DurationText = duration
        };
    }
}
