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

public sealed class HeaderAction
{
    public HeaderAction(string text, string iconCode, ICommand command, bool isPrimary = false)
    {
        Text = text;
        IconCode = iconCode;
        Command = command;
        IsPrimary = isPrimary;
    }

    public HeaderAction(string text, string iconCode, object flyoutContent)
    {
        Text = text;
        IconCode = iconCode;
        FlyoutContent = flyoutContent;
    }

    public string Text { get; }
    public string IconCode { get; }
    public ICommand? Command { get; }
    public bool IsPrimary { get; }
    public object? FlyoutContent { get; }
    public bool HasFlyout => FlyoutContent is not null;
    public bool IsSecondaryCommand => !IsPrimary && !HasFlyout;
}
