using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetFleet.Tests;

public sealed class ProjectIconStoreTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"fleet-icons-{Guid.NewGuid():N}");

    public ProjectIconStoreTests()
    {
        Directory.CreateDirectory(tempDir);
    }

    [Fact]
    public async Task ResolveFromCheckout_UsesFirstConfiguredPackageProjectIcon()
    {
        var checkout = CreateCheckout();
        var firstIcon = new byte[] { 1, 2, 3 };
        var secondIcon = new byte[] { 4, 5, 6 };
        WriteProjectWithPackageIcon(checkout, "src/First/First.csproj", "first.png", firstIcon);
        WriteProjectWithPackageIcon(checkout, "src/Second/Second.csproj", "second.png", secondIcon);
        WriteDeployerYaml(checkout, "src/First/First.csproj", "src/Second/Second.csproj");
        var store = CreateStore();

        var icon = await store.ResolveFromCheckout(new Project(), checkout, CancellationToken.None);

        icon.Should().NotBeNull();
        icon!.Bytes.Should().Equal(firstIcon);
        icon.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task ResolveFromCheckout_PrefersPngConventionWhenApplicationIconIsIco()
    {
        var checkout = CreateCheckout();
        var projectDir = Directory.CreateDirectory(Path.Combine(checkout, "src", "App.Desktop")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(projectDir, "icon.ico"), [9, 9, 9]);
        var png = new byte[] { 7, 8, 9 };
        await File.WriteAllBytesAsync(Path.Combine(projectDir, "icon.png"), png);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "App.Desktop.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <ApplicationIcon>icon.ico</ApplicationIcon>
              </PropertyGroup>
            </Project>
            """);
        WriteDeployerYaml(checkout, "src/App.Desktop/App.Desktop.csproj");
        var store = CreateStore();

        var icon = await store.ResolveFromCheckout(new Project(), checkout, CancellationToken.None);

        icon.Should().NotBeNull();
        icon!.Bytes.Should().Equal(png);
        icon.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task ResolveFromCheckout_RejectsIconsOutsideCheckout()
    {
        var checkout = CreateCheckout();
        var outsideDir = Directory.CreateDirectory(Path.Combine(tempDir, "outside")).FullName;
        var outsideIcon = Path.Combine(outsideDir, "outside.png");
        await File.WriteAllBytesAsync(outsideIcon, [1, 2, 3]);
        var projectDir = Directory.CreateDirectory(Path.Combine(checkout, "src", "App")).FullName;
        await File.WriteAllTextAsync(Path.Combine(projectDir, "App.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <PackageIcon>{{Path.GetRelativePath(projectDir, outsideIcon)}}</PackageIcon>
              </PropertyGroup>
            </Project>
            """);
        WriteDeployerYaml(checkout, "src/App/App.csproj");
        var store = CreateStore();

        var icon = await store.ResolveFromCheckout(new Project(), checkout, CancellationToken.None);

        icon.Should().BeNull();
    }

    [Fact]
    public async Task Invalidate_RemovesCachedIcon()
    {
        var projectId = Guid.NewGuid();
        var store = CreateStore();
        await store.Cache(projectId, new ProjectIcon([1, 2, 3], "image/png", ".png"), CancellationToken.None);

        await store.Invalidate(projectId, CancellationToken.None);

        var cached = await store.TryReadCached(projectId, CancellationToken.None);
        cached.Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
    }

    private ProjectIconStore CreateStore() =>
        new(Path.Combine(tempDir, "cache"), NullLogger<ProjectIconStore>.Instance);

    private string CreateCheckout()
    {
        var path = Path.Combine(tempDir, $"checkout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private static void WriteProjectWithPackageIcon(string checkout, string projectPath, string iconName, byte[] iconBytes)
    {
        var fullProjectPath = Path.Combine(checkout, projectPath);
        var projectDir = Path.GetDirectoryName(fullProjectPath)!;
        Directory.CreateDirectory(projectDir);
        File.WriteAllBytes(Path.Combine(projectDir, iconName), iconBytes);
        File.WriteAllText(fullProjectPath, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <PackageIcon>{{iconName}}</PackageIcon>
              </PropertyGroup>
            </Project>
            """);
    }

    private static void WriteDeployerYaml(string checkout, params string[] projects)
    {
        var packageLines = string.Join(
            Environment.NewLine,
            projects.Select(project => $"    - project: {project}"));
        File.WriteAllText(Path.Combine(checkout, "deployer.yaml"), $"""
            github:
              packages:
            {packageLines}
            """);
    }
}
