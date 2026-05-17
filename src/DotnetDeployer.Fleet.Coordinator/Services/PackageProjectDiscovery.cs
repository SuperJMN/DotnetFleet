using DotnetDeployer.Fleet.Core.Domain;
using YamlDotNet.RepresentationModel;

namespace DotnetDeployer.Fleet.Coordinator.Services;

public sealed class PackageProjectDiscovery
{
    public async Task<IReadOnlyList<string>> DiscoverPackageProjectsAsync(Project project, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dotnetfleet-package-projects", $"{project.Id:N}-{Guid.NewGuid():N}");

        try
        {
            await ProjectRepositoryCheckout.CloneShallow(project, tempDir, ct);

            var deployerYaml = FindDeployerYaml(tempDir);
            if (deployerYaml is null)
                return [];

            var yaml = await File.ReadAllTextAsync(deployerYaml, ct);
            return ReadProjectsFromYaml(yaml);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup. Discovery is advisory; temp cleanup must not break the request.
            }
        }
    }

    internal static IReadOnlyList<string> ReadProjectsFromYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return [];

        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);

        if (stream.Documents.Count == 0 ||
            stream.Documents[0].RootNode is not YamlMappingNode root ||
            !TryGetMappingValue(root, "github", out var githubNode) ||
            githubNode is not YamlMappingNode github ||
            !TryGetMappingValue(github, "packages", out var packagesNode) ||
            packagesNode is not YamlSequenceNode packages)
        {
            return [];
        }

        return packages
            .Children
            .OfType<YamlMappingNode>()
            .Select(ReadProjectValue)
            .Where(project => !string.IsNullOrWhiteSpace(project))
            .Select(project => project!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ReadProjectValue(YamlMappingNode package)
    {
        return TryGetMappingValue(package, "project", out var projectNode) &&
               projectNode is YamlScalarNode project
            ? project.Value?.Trim()
            : null;
    }

    private static bool TryGetMappingValue(YamlMappingNode mapping, string key, out YamlNode value)
    {
        foreach (var child in mapping.Children)
        {
            if (child.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                value = child.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private static string? FindDeployerYaml(string root)
    {
        var yaml = Path.Combine(root, "deployer.yaml");
        if (File.Exists(yaml))
            return yaml;

        var yml = Path.Combine(root, "deployer.yml");
        return File.Exists(yml) ? yml : null;
    }
}
