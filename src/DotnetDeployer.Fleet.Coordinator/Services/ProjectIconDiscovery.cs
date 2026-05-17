namespace DotnetDeployer.Fleet.Coordinator.Services;

internal static class ProjectIconDiscovery
{
    public static async Task<IReadOnlyList<string>> ConfiguredPackageProjectFiles(string checkoutRoot, CancellationToken ct)
    {
        var deployerYaml = FindDeployerYaml(checkoutRoot);
        if (deployerYaml is null)
            return [];

        var yaml = await File.ReadAllTextAsync(deployerYaml, ct);
        return PackageProjectDiscovery.ReadProjectsFromYaml(yaml)
            .Select(project => ResolveCheckoutPath(checkoutRoot, project))
            .Where(projectFile => projectFile is not null && File.Exists(projectFile))
            .Select(projectFile => projectFile!)
            .ToList();
    }

    public static IReadOnlyList<string> DiscoverProjectFiles(string checkoutRoot)
    {
        if (!Directory.Exists(checkoutRoot))
            return [];

        return Directory.EnumerateFiles(checkoutRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => IsInside(checkoutRoot, path))
            .Where(path => !IsIgnoredProjectPath(checkoutRoot, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindDeployerYaml(string root)
    {
        var yaml = Path.Combine(root, "deployer.yaml");
        if (File.Exists(yaml))
            return yaml;

        var yml = Path.Combine(root, "deployer.yml");
        return File.Exists(yml) ? yml : null;
    }

    private static string? ResolveCheckoutPath(string checkoutRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(checkoutRoot, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
        return IsInside(checkoutRoot, fullPath) ? fullPath : null;
    }

    private static bool IsIgnoredProjectPath(string checkoutRoot, string projectPath)
    {
        var relative = Path.GetRelativePath(checkoutRoot, projectPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        return relative == "."
               || (!relative.StartsWith("..", StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative));
    }
}
