using System.Diagnostics;
using DotnetFleet.Core.Domain;
using YamlDotNet.RepresentationModel;

namespace DotnetFleet.Coordinator.Services;

public sealed class PackageProjectDiscovery
{
    public async Task<IReadOnlyList<string>> DiscoverPackageProjectsAsync(Project project, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dotnetfleet-package-projects", $"{project.Id:N}-{Guid.NewGuid():N}");

        try
        {
            await CloneShallowAsync(project, tempDir, ct);

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

    private static async Task CloneShallowAsync(Project project, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add("--depth");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--branch");
        psi.ArgumentList.Add(project.Branch);
        psi.ArgumentList.Add(InjectToken(project.GitUrl, project.GitToken));
        psi.ArgumentList.Add(destination);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git.");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        _ = await stdout;
        _ = await stderr;

        if (process.ExitCode != 0)
            throw new InvalidOperationException("Unable to clone the project repository to discover package projects.");
    }

    private static string InjectToken(string gitUrl, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return gitUrl;
        if (!Uri.TryCreate(gitUrl, UriKind.Absolute, out var uri)) return gitUrl;
        if (uri.Scheme is not ("http" or "https")) return gitUrl;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return gitUrl;

        var encoded = Uri.EscapeDataString(token);
        var builder = new UriBuilder(uri)
        {
            UserName = "x-access-token",
            Password = encoded
        };
        return builder.Uri.ToString();
    }
}
