using DotnetFleet.Core.Domain;
using System.Security.Cryptography;

namespace DotnetFleet.Coordinator.Services;

public sealed class PackageArtifactStore
{
    private readonly string rootDir;

    public PackageArtifactStore(string rootDir)
    {
        this.rootDir = Path.GetFullPath(rootDir);
        Directory.CreateDirectory(this.rootDir);
    }

    public async Task<PackageArtifact> SaveAsync(
        DeploymentJob job,
        string relativePath,
        Stream content,
        CancellationToken ct = default)
    {
        var safeRelativePath = NormalizeRelativePath(relativePath);
        var destination = GetArtifactPath(job, safeRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using (var output = File.Create(destination))
        {
            await content.CopyToAsync(output, ct);
        }

        return ToArtifactInfo(job, destination);
    }

    public Task<IReadOnlyList<PackageArtifact>> ListAsync(DeploymentJob job, CancellationToken ct = default)
    {
        var dir = GetJobDir(job);
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<PackageArtifact>>([]);

        var artifacts = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(path => ToArtifactInfo(job, path))
            .OrderBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<PackageArtifact>>(artifacts);
    }

    public FileStream OpenRead(DeploymentJob job, string relativePath)
    {
        var safeRelativePath = NormalizeRelativePath(relativePath);
        var path = GetArtifactPath(job, safeRelativePath);
        return File.OpenRead(path);
    }

    public string GetFileName(string relativePath) =>
        Path.GetFileName(NormalizeRelativePath(relativePath));

    public Task DeleteAsync(DeploymentJob job, CancellationToken ct = default)
    {
        var dir = GetJobDir(job);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);

        return Task.CompletedTask;
    }

    private PackageArtifact ToArtifactInfo(DeploymentJob job, string path)
    {
        var info = new FileInfo(path);
        var relative = Path.GetRelativePath(GetJobDir(job), path)
            .Replace(Path.DirectorySeparatorChar, '/');

        return new PackageArtifact
        {
            FileName = info.Name,
            RelativePath = relative,
            SizeBytes = info.Length,
            Sha256 = ComputeSha256(path),
            CreatedAt = info.CreationTimeUtc
        };
    }

    private static string ComputeSha256(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private string GetArtifactPath(DeploymentJob job, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(GetJobDir(job), relativePath));
        var jobDir = GetJobDir(job);

        if (!path.StartsWith(jobDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(path, jobDir, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Artifact path escapes the job artifact directory.");
        }

        return path;
    }

    private string GetJobDir(DeploymentJob job) =>
        Path.Combine(rootDir, job.ProjectId.ToString("N"), job.Id.ToString("N"));

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Artifact path is required.", nameof(relativePath));

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0
            || Path.IsPathRooted(normalized)
            || segments.Any(s => s is "." or ".."))
        {
            throw new ArgumentException("Artifact path must be relative and cannot contain traversal segments.", nameof(relativePath));
        }

        return Path.Combine(segments);
    }
}
