using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotnetFleet.Tool;

/// <summary>
/// Detects host-level packages that worker deployments are likely to need
/// and reports any that are missing. Does not install anything — the
/// installer only surfaces a copy-pasteable apt/dnf/pacman command so the
/// operator can decide.
///
/// <para>
/// Scope: only the truly system-level basics (git/curl/unzip). The Android
/// AOT toolchain (LLVM) and the JDK/Android SDK are auto-provisioned by
/// DotnetDeployer at deploy time without needing root, so they are
/// deliberately not listed here.
/// </para>
/// </summary>
internal static class WorkerPrerequisitesChecker
{
    /// <summary>
    /// Inspects the host and prints a warning section listing missing
    /// dependencies (if any) plus the suggested install command for the
    /// detected package manager.
    /// </summary>
    public static void ReportMissingDependencies()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var dependencies = ExpectedDependencies().ToList();
        var missing = dependencies.Where(d => !d.Probe()).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var pm = DetectPackageManager();

        Console.WriteLine();
        Console.WriteLine("  ⚠ Missing host packages that deployments may require:");
        foreach (var dep in missing)
        {
            Console.WriteLine($"      - {dep.DisplayName}: {dep.Reason}");
        }

        Console.WriteLine();
        if (pm is null)
        {
            Console.WriteLine("    No supported package manager found (apt/dnf/pacman).");
            Console.WriteLine("    Install the listed dependencies manually.");
            Console.WriteLine();
            return;
        }

        var packages = missing
            .SelectMany(d => d.PackagesFor(pm.Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (packages.Count == 0)
        {
            return;
        }

        Console.WriteLine($"    Suggested install ({pm.Value.ToString().ToLowerInvariant()}):");
        Console.WriteLine($"      sudo {InstallCommandFor(pm.Value, packages)}");
        Console.WriteLine();
    }

    // ── Dependency catalogue ──────────────────────────────────────────────

    private static IEnumerable<Dependency> ExpectedDependencies()
    {
        yield return new Dependency(
            DisplayName: "git",
            Reason: "required to clone project repositories",
            Probe: () => HasBinaryOnPath("git"),
            Packages: new()
            {
                [PackageManager.Apt] = ["git"],
                [PackageManager.Dnf] = ["git"],
                [PackageManager.Pacman] = ["git"],
            });

        yield return new Dependency(
            DisplayName: "curl",
            Reason: "used by deployer scripts and shim bootstrap",
            Probe: () => HasBinaryOnPath("curl"),
            Packages: new()
            {
                [PackageManager.Apt] = ["curl"],
                [PackageManager.Dnf] = ["curl"],
                [PackageManager.Pacman] = ["curl"],
            });

        yield return new Dependency(
            DisplayName: "unzip",
            Reason: "required by the .NET Android workload installer",
            Probe: () => HasBinaryOnPath("unzip"),
            Packages: new()
            {
                [PackageManager.Apt] = ["unzip"],
                [PackageManager.Dnf] = ["unzip"],
                [PackageManager.Pacman] = ["unzip"],
            });
    }

    // ── Probes ────────────────────────────────────────────────────────────

    private static bool HasBinaryOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore unreadable PATH entries.
            }
        }

        return false;
    }

    // ── Package manager detection ─────────────────────────────────────────

    internal enum PackageManager { Apt, Dnf, Pacman }

    private static PackageManager? DetectPackageManager()
    {
        if (HasBinaryOnPath("apt-get")) return PackageManager.Apt;
        if (HasBinaryOnPath("dnf")) return PackageManager.Dnf;
        if (HasBinaryOnPath("pacman")) return PackageManager.Pacman;
        return null;
    }

    internal static string InstallCommandFor(PackageManager pm, IReadOnlyList<string> packages)
    {
        var joined = string.Join(' ', packages);
        return pm switch
        {
            PackageManager.Apt => $"apt-get install -y {joined}",
            PackageManager.Dnf => $"dnf install -y {joined}",
            PackageManager.Pacman => $"pacman -S --noconfirm {joined}",
            _ => $"<install> {joined}"
        };
    }

    // ── Helper record ─────────────────────────────────────────────────────

    private sealed record Dependency(
        string DisplayName,
        string Reason,
        Func<bool> Probe,
        Dictionary<PackageManager, string[]> Packages)
    {
        public string[] PackagesFor(PackageManager pm) =>
            Packages.TryGetValue(pm, out var pkgs) ? pkgs : [];
    }
}
