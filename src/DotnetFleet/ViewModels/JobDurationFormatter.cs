namespace DotnetFleet.ViewModels;

internal static class JobDurationFormatter
{
    public static string Format(long? durationMs)
    {
        if (durationMs is null)
            return "--";

        var duration = TimeSpan.FromMilliseconds(Math.Max(0, durationMs.Value));
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours:D2}h {duration.Minutes:D2}m";

        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";

        return $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";
    }
}
