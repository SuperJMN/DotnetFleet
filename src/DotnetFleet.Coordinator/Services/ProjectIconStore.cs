using DotnetFleet.Core.Domain;
using DotnetProjectKit;
using Microsoft.Extensions.Logging;

namespace DotnetFleet.Coordinator.Services;

public sealed record ProjectIcon(byte[] Bytes, string ContentType, string Extension);

public sealed class ProjectIconStore
{
    internal const long MaxIconBytes = 1_000_000;
    private readonly string rootDir;
    private readonly ILogger<ProjectIconStore> logger;

    public ProjectIconStore(string rootDir, ILogger<ProjectIconStore> logger)
    {
        this.rootDir = rootDir;
        this.logger = logger;
    }

    public async Task<ProjectIcon?> GetOrResolve(Project project, CancellationToken ct = default)
    {
        var cached = await TryReadCached(project.Id, ct);
        if (cached is not null)
            return cached;

        var tempDir = Path.Combine(Path.GetTempPath(), "dotnetfleet-project-icons", $"{project.Id:N}-{Guid.NewGuid():N}");
        try
        {
            await ProjectRepositoryCheckout.CloneShallow(project, tempDir, ct);
            var icon = await ResolveFromCheckout(project, tempDir, ct);
            if (icon is null)
                return null;

            await Cache(project.Id, icon, ct);
            return icon;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Project icon autodetection failed for {ProjectName}", project.Name);
            return null;
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
                // Best-effort cleanup. Icon discovery is advisory.
            }
        }
    }

    internal async Task<ProjectIcon?> ResolveFromCheckout(Project project, string checkoutRoot, CancellationToken ct)
    {
        var deployerYaml = FindDeployerYaml(checkoutRoot);
        if (deployerYaml is null)
            return null;

        var yaml = await File.ReadAllTextAsync(deployerYaml, ct);
        var packageProjects = PackageProjectDiscovery.ReadProjectsFromYaml(yaml);
        foreach (var packageProject in packageProjects)
        {
            var projectFile = ResolveCheckoutPath(checkoutRoot, packageProject);
            if (projectFile is null || !File.Exists(projectFile))
                continue;

            var icon = await ResolveProjectIcon(checkoutRoot, projectFile, ct);
            if (icon is not null)
            {
                logger.LogDebug("Resolved icon for {ProjectName} from {ProjectFile}", project.Name, projectFile);
                return icon;
            }
        }

        return null;
    }

    internal async Task<ProjectIcon?> TryReadCached(Guid projectId, CancellationToken ct = default)
    {
        var directory = ProjectDirectory(projectId);
        if (!Directory.Exists(directory))
            return null;

        var file = Directory.EnumerateFiles(directory, "icon.*")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (file is null)
            return null;

        var extension = Path.GetExtension(file);
        var contentType = ContentTypeFor(extension);
        if (contentType is null)
            return null;

        var info = new FileInfo(file);
        if (info.Length > MaxIconBytes)
            return null;

        return new ProjectIcon(await File.ReadAllBytesAsync(file, ct), contentType, extension);
    }

    internal async Task Cache(Guid projectId, ProjectIcon icon, CancellationToken ct = default)
    {
        var directory = ProjectDirectory(projectId);
        Directory.CreateDirectory(directory);

        foreach (var file in Directory.EnumerateFiles(directory, "icon.*"))
            File.Delete(file);

        await File.WriteAllBytesAsync(Path.Combine(directory, "icon" + icon.Extension), icon.Bytes, ct);
    }

    public Task Invalidate(Guid projectId, CancellationToken ct = default)
    {
        _ = ct;
        var directory = ProjectDirectory(projectId);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        return Task.CompletedTask;
    }

    private async Task<ProjectIcon?> ResolveProjectIcon(string checkoutRoot, string projectPath, CancellationToken ct)
    {
        var metadataResult = new ProjectMetadataReader().Read(new FileInfo(projectPath), Serilog.Core.Logger.None);
        if (metadataResult.IsFailure)
        {
            logger.LogDebug("Unable to read project metadata from {ProjectPath}: {Error}", projectPath, metadataResult.Error);
            return null;
        }

        var resolver = new ProjectAssetResolver();
        var projectFile = new FileInfo(projectPath);
        var resolved = resolver.ResolveIcon(projectFile, metadataResult.Value, IsPreferredUiIcon, Serilog.Core.Logger.None)
            ?? resolver.ResolveIcon(projectFile, metadataResult.Value, IsSupportedUiIcon, Serilog.Core.Logger.None);

        if (resolved is null || !IsInside(checkoutRoot, resolved.Path))
            return null;

        var info = new FileInfo(resolved.Path);
        if (!info.Exists || info.Length > MaxIconBytes)
            return null;

        var extension = Path.GetExtension(info.FullName);
        var contentType = ContentTypeFor(extension);
        if (contentType is null)
            return null;

        return new ProjectIcon(await File.ReadAllBytesAsync(info.FullName, ct), contentType, extension.ToLowerInvariant());
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

    private static bool IsInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        return relative == "."
               || (!relative.StartsWith("..", StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative));
    }

    private static bool IsPreferredUiIcon(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedUiIcon(string path)
    {
        return IsPreferredUiIcon(path)
               || string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ContentTypeFor(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico" => "image/x-icon",
            _ => null
        };
    }

    private string ProjectDirectory(Guid projectId) =>
        Path.Combine(rootDir, projectId.ToString("N"));
}
