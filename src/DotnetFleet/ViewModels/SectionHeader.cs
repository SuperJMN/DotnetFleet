using System.Windows.Input;

namespace DotnetFleet.ViewModels;

public sealed class SectionHeader
{
    public SectionHeader(string title, params HeaderAction[] actions)
        : this(title, subtitle: null, actions)
    {
    }

    public SectionHeader(string title, string? subtitle, params HeaderAction[] actions)
    {
        Title = title;
        Subtitle = subtitle;
        Actions = actions;
    }

    public string Title { get; }
    public string? Subtitle { get; }
    public IReadOnlyList<HeaderAction> Actions { get; }
}

public sealed record HeaderAction(string Text, string IconCode, ICommand Command, bool IsPrimary = false);
