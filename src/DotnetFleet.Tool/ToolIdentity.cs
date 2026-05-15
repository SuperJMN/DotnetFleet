using System.Reflection;

namespace DotnetFleet.Tool;

internal static class ToolIdentity
{
    public const string ProductName = "DotnetDeployer.Fleet";
    public const string CompatibilityProductName = "DotnetFleet";
    public const string PrimaryPackageId = "DotnetDeployer.Fleet.Tool";
    public const string CompatibilityPackageId = "DotnetFleet.Tool";
    public const string CommandName = "fleet";

    public static readonly IReadOnlyList<string> KnownPackageIds =
    [
        PrimaryPackageId,
        CompatibilityPackageId
    ];

    public static string CurrentPackageId =>
        GetAssemblyPackageId()
        ?? ExtractPackageIdFromPath(Environment.ProcessPath)
        ?? PrimaryPackageId;

    public static bool IsKnownPackageId(string value) =>
        KnownPackageIds.Any(packageId => string.Equals(packageId, value, StringComparison.OrdinalIgnoreCase));

    public static string? ExtractPackageIdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = path.Replace('\\', '/');
        foreach (var packageId in KnownPackageIds)
        {
            var marker = "/" + packageId + "/";
            if (normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return packageId;
        }

        return null;
    }

    public static string? ExtractVersionFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = path.Replace('\\', '/');
        foreach (var packageId in KnownPackageIds)
        {
            var marker = "/" + packageId + "/";
            var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;

            var rest = normalized[(idx + marker.Length)..];
            var slash = rest.IndexOf('/');
            if (slash <= 0)
                return null;

            var candidate = rest[..slash];
            return candidate.Length > 0 && char.IsDigit(candidate[0]) ? candidate : null;
        }

        return null;
    }

    public static string? FindInstalledVersion(string toolListOutput, string packageId)
    {
        foreach (var line in toolListOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[0], packageId, StringComparison.OrdinalIgnoreCase))
                return parts[1];
        }

        return null;
    }

    public static string? FindInstalledKnownPackageId(string toolListOutput)
    {
        foreach (var line in toolListOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var packageId = KnownPackageIds.FirstOrDefault(packageId =>
                string.Equals(packageId, parts[0], StringComparison.OrdinalIgnoreCase));
            if (packageId != null)
                return packageId;
        }

        return null;
    }

    private static string? GetAssemblyPackageId()
    {
        return typeof(ToolIdentity)
            .Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(metadata => metadata.Key == "NuGetPackageId")
            ?.Value;
    }
}
