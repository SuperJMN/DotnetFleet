namespace DotnetFleet.ViewModels;

internal static class VersionDisplay
{
    public static string? Visible(string? version)
    {
        if (version is null)
            return null;

        var metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex < 0 ? version : version[..metadataIndex];
    }

    public static string? VisibleWithPrefix(string? version)
    {
        var visible = Visible(version);
        return visible is null || visible.Length == 0 ? visible : $"v{visible}";
    }
}
