using System.Text;
using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;

namespace DotnetFleet.Tests;

public class PackageArtifactStoreTests : IDisposable
{
    private readonly string rootDir = Path.Combine(Path.GetTempPath(), $"fleet-artifacts-{Guid.NewGuid():N}");
    private readonly DeploymentJob job = new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Kind = JobKind.PackageBuild
    };

    [Fact]
    public async Task SaveAsync_stores_artifact_under_project_and_job()
    {
        var store = new PackageArtifactStore(rootDir);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("payload"));

        var saved = await store.SaveAsync(job, "windows/setup-x64.exe", content);
        var listed = await store.ListAsync(job);

        saved.RelativePath.Should().Be("windows/setup-x64.exe");
        saved.Sha256.Should().Be("239f59ed55e737c77147cf55ad0c1b030b6d7ee748a7426952f9b852d5a935e5");
        listed.Should().ContainSingle(a => a.RelativePath == "windows/setup-x64.exe");

        await using var read = store.OpenRead(job, "windows/setup-x64.exe");
        using var reader = new StreamReader(read);
        (await reader.ReadToEndAsync()).Should().Be("payload");
    }

    [Fact]
    public async Task SaveAsync_rejects_path_traversal()
    {
        var store = new PackageArtifactStore(rootDir);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("payload"));

        var act = async () => await store.SaveAsync(job, "../escape.exe", content);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeleteAsync_removes_artifacts_for_job()
    {
        var store = new PackageArtifactStore(rootDir);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        await store.SaveAsync(job, "windows/setup-x64.exe", content);

        await store.DeleteAsync(job);

        var listed = await store.ListAsync(job);
        listed.Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(rootDir, recursive: true); } catch { /* best effort */ }
    }
}
