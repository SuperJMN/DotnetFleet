namespace DotnetFleet.WorkerService.RepoStorage;

/// <summary>
/// Writes "barrier" MSBuild files inside the repo storage path so that cloned
/// target repos never inherit DotnetFleet's own root <c>Directory.Packages.props</c>
/// (Central Package Management) when the worker happens to be running from
/// inside the DotnetFleet source tree.
/// </summary>
/// <remarks>
/// MSBuild walks upward from a <c>.csproj</c> looking for
/// <c>Directory.Packages.props</c> / <c>Directory.Build.props</c> and stops at
/// the first one it finds. Placing both files at the root of
/// <see cref="EnsureBarrierFiles"/>'s <c>repoStoragePath</c> guarantees the
/// search ends there, before reaching any ancestor that might enable CPM and
/// break inline <c>PackageReference</c> versions (NU1008) on guest projects.
/// Repos that themselves use CPM are unaffected: their own
/// <c>Directory.Packages.props</c> sits closer to the csproj and wins.
/// </remarks>
public static class RepoStorageIsolator
{
    private const string ManagedHeader =
        "<!-- Managed by DotnetFleet. Do not edit; this file is regenerated on worker startup. -->";

    private const string PackagesPropsContent = $"""
        {ManagedHeader}
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
          </PropertyGroup>
        </Project>
        """;

    private const string BuildPropsContent = $"""
        {ManagedHeader}
        <Project>
        </Project>
        """;

    /// <summary>
    /// Writes <c>Directory.Packages.props</c> and <c>Directory.Build.props</c>
    /// inside <paramref name="repoStoragePath"/>, creating the directory if
    /// needed. Always overwrites: the files are owned by DotnetFleet.
    /// </summary>
    public static void EnsureBarrierFiles(string repoStoragePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoStoragePath);
        Directory.CreateDirectory(repoStoragePath);

        File.WriteAllText(
            Path.Combine(repoStoragePath, "Directory.Packages.props"),
            PackagesPropsContent);

        File.WriteAllText(
            Path.Combine(repoStoragePath, "Directory.Build.props"),
            BuildPropsContent);
    }
}
