using System.Collections.ObjectModel;
using DotnetFleet.Core.Domain;

namespace DotnetFleet.ViewModels;

/// <summary>
/// View-model row for a single <see cref="JobPhase"/>. Wraps the domain entity
/// with display-friendly properties (icon by status, formatted duration,
/// localised display name) so the AXAML stays declarative. <see cref="Children"/>
/// holds inner phases nested via interval containment, consumed by the TreeView's
/// HierarchicalDataTemplate.
/// </summary>
public class JobPhaseRow
{
    public string Icon { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string DurationText { get; init; } = "";
    public ObservableCollection<JobPhaseRow> Children { get; } = new();

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

    /// <summary>
    /// Builds a forest of rows in chronological order, nesting each phase under
    /// the most recent still-open parent (timestamp containment). Mirrors the
    /// LIFO stack semantics already used by the coordinator.
    /// </summary>
    public static IReadOnlyList<JobPhaseRow> BuildHierarchy(
        IReadOnlyList<JobPhase> phases,
        Func<string, string> formatName)
    {
        var ordered = phases
            .OrderBy(p => p.StartedAt)
            .ThenBy(p => p.EndedAt ?? DateTimeOffset.MaxValue)
            .ToList();

        var roots = new List<JobPhaseRow>();
        var stack = new Stack<(JobPhase Phase, JobPhaseRow Row)>();

        foreach (var phase in ordered)
        {
            while (stack.Count > 0 && !Contains(stack.Peek().Phase, phase))
                stack.Pop();

            var row = From(phase, formatName);
            if (stack.Count == 0)
                roots.Add(row);
            else
                stack.Peek().Row.Children.Add(row);

            stack.Push((phase, row));
        }

        return roots;
    }

    private static bool Contains(JobPhase outer, JobPhase inner)
    {
        if (inner.StartedAt < outer.StartedAt) return false;
        if (outer.EndedAt is null) return true;
        var innerEnd = inner.EndedAt ?? inner.StartedAt;
        return innerEnd <= outer.EndedAt.Value;
    }
}
