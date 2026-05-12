using System.Diagnostics;
using System.Security.Principal;

namespace DotnetFleet.Tool;

internal static class WindowsServiceManager
{
    private const string RootFolderName = "DotnetFleet";
    private const string CoordinatorServiceName = "fleet-coordinator";

    public static string WorkerServiceName(string workerName) => $"fleet-worker-{workerName}";

    public static string GetDefaultWindowsDataDir(string component, string? programDataRoot = null)
    {
        var root = programDataRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "ProgramData");
        }

        if (root.Contains('\\') && !root.Contains('/'))
            return string.Join('\\', root.TrimEnd('\\'), RootFolderName, component);

        return Path.Combine(root, RootFolderName, component);
    }

    public static string BuildCoordinatorImagePath(string fleetPath, ServiceInstaller.CoordinatorInstallOptions opts)
    {
        return $"{ServiceCommandLine.QuoteArgument(fleetPath)} coordinator {ServiceCommandLine.BuildCoordinatorArgs(opts)}";
    }

    public static string BuildWorkerImagePath(string fleetPath, ServiceInstaller.WorkerInstallOptions opts)
    {
        return $"{ServiceCommandLine.QuoteArgument(fleetPath)} worker {ServiceCommandLine.BuildWorkerArgs(opts)}";
    }

    public static LocalCoordinatorDiscovery.Result? TryDiscoverCoordinatorFromInstalledService()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var imagePath = ReadServiceImagePath(CoordinatorServiceName);
        return imagePath is null ? null : TryDiscoverCoordinatorFromImagePath(imagePath);
    }

    public static LocalCoordinatorDiscovery.Result? TryDiscoverCoordinatorFromImagePath(string imagePath)
    {
        var args = ServiceCommandLine.Split(imagePath);
        var dataDir = ServiceCommandLine.GetOptionValue(args, "--data-dir");
        if (string.IsNullOrWhiteSpace(dataDir))
            return null;

        return LocalCoordinatorDiscovery.TryLoadFromDataDir(dataDir, "Windows service ImagePath");
    }

    public static async Task InstallCoordinatorAsync(ServiceInstaller.CoordinatorInstallOptions opts)
    {
        EnsureWindows();
        EnsureAdministrator();

        Directory.CreateDirectory(opts.DataDir);
        var config = FleetConfig.LoadOrCreateCoordinatorConfig(
            opts.DataDir, opts.JwtSecret, opts.Token, opts.Port);

        var fleetPath = await EnsureFleetServiceToolAsync();
        var imagePath = BuildCoordinatorImagePath(fleetPath, opts);
        await InstallServiceAsync(
            CoordinatorServiceName,
            "DotnetFleet Coordinator",
            "DotnetFleet Coordinator",
            imagePath);

        Console.WriteLine();
        Console.WriteLine($"  ✓ Coordinator installed as Windows service '{CoordinatorServiceName}'");
        Console.WriteLine($"    Listening on port {opts.Port}");
        Console.WriteLine($"    Registration token: {config.RegistrationToken}");
        Console.WriteLine($"    Data directory: {opts.DataDir}");
        Console.WriteLine();
        Console.WriteLine("  Connect workers with:");
        Console.WriteLine($"    fleet worker install --coordinator http://<host>:{opts.Port} --token {config.RegistrationToken}");
        Console.WriteLine();
        Console.WriteLine("  Manage with:");
        Console.WriteLine($"    Get-Service {CoordinatorServiceName}");
        Console.WriteLine($"    Stop-Service {CoordinatorServiceName}");
        Console.WriteLine("    fleet coordinator uninstall");
        Console.WriteLine();
    }

    public static async Task UninstallCoordinatorAsync()
    {
        EnsureWindows();
        EnsureAdministrator();
        await UninstallServiceAsync(CoordinatorServiceName);
        Console.WriteLine($"  ✓ Coordinator service '{CoordinatorServiceName}' removed.");
    }

    public static async Task CoordinatorStatusAsync()
    {
        EnsureWindows();
        await ShowStatusAsync(CoordinatorServiceName);

        var discovered = TryDiscoverCoordinatorFromInstalledService();
        if (discovered != null)
        {
            Console.WriteLine($"  Token: {discovered.Token}");
            Console.WriteLine($"  URL:   {discovered.Url}");
            Console.WriteLine();
        }
    }

    public static async Task InstallWorkerAsync(ServiceInstaller.WorkerInstallOptions opts)
    {
        EnsureWindows();
        EnsureAdministrator();

        Directory.CreateDirectory(opts.DataDir);
        var fleetPath = await EnsureFleetServiceToolAsync();
        var serviceName = WorkerServiceName(opts.Name);
        var imagePath = BuildWorkerImagePath(fleetPath, opts);

        await InstallServiceAsync(
            serviceName,
            $"DotnetFleet Worker ({opts.Name})",
            $"DotnetFleet Worker ({opts.Name})",
            imagePath);

        Console.WriteLine();
        Console.WriteLine($"  ✓ Worker '{opts.Name}' installed as Windows service '{serviceName}'");
        Console.WriteLine($"    Coordinator: {opts.CoordinatorUrl}");
        Console.WriteLine($"    Data directory: {opts.DataDir}");
        Console.WriteLine();
        Console.WriteLine("  Manage with:");
        Console.WriteLine($"    Get-Service {serviceName}");
        Console.WriteLine($"    Stop-Service {serviceName}");
        Console.WriteLine($"    fleet worker uninstall --name {opts.Name}");
        Console.WriteLine();
    }

    public static async Task UninstallWorkerAsync(string workerName)
    {
        EnsureWindows();
        EnsureAdministrator();
        var serviceName = WorkerServiceName(workerName);
        await UninstallServiceAsync(serviceName);
        Console.WriteLine($"  ✓ Worker service '{serviceName}' removed.");
    }

    public static async Task WorkerStatusAsync(string workerName)
    {
        EnsureWindows();
        await ShowStatusAsync(WorkerServiceName(workerName));
    }

    public static async Task UpdateLocalServicesAsync(ServiceInstaller.UpdateOptions opts)
    {
        EnsureWindows();
        EnsureAdministrator();

        var installedServices = await DiscoverInstalledFleetServicesAsync();

        Console.WriteLine();
        Console.WriteLine("  Updating DotnetFleet on this machine");
        Console.WriteLine("  ────────────────────────────────────");
        if (installedServices.Count == 0)
        {
            Console.WriteLine("  (no local fleet services found)");
        }
        else
        {
            foreach (var svc in installedServices)
                Console.WriteLine($"    • {svc}");
        }
        Console.WriteLine();

        var servicesStoppedForUpdate = false;
        if (!opts.SkipToolUpdate && installedServices.Count > 0)
        {
            Console.WriteLine("  Stopping services before updating the service tool...");
            foreach (var serviceName in installedServices)
                await StopServiceAsync(serviceName);
            servicesStoppedForUpdate = true;
            Console.WriteLine();
        }

        if (!opts.SkipToolUpdate)
        {
            var versionBefore = await GetInstalledServiceToolVersionAsync();
            var verb = File.Exists(Path.Combine(GetServiceToolsDir(), "fleet.exe")) ? "update" : "install";
            var exitCode = await RunDotnetToolPathCommandAsync(verb, opts.Version, opts.IncludePrerelease);
            if (exitCode != 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"  ✗ 'dotnet tool {verb}' failed. Services were not restarted.");
                Console.Error.WriteLine("    Re-run with --skip-tool-update to only restart services.");
                if (servicesStoppedForUpdate)
                    await StartServicesBestEffortAsync(installedServices);
                throw new InvalidOperationException($"dotnet tool {verb} failed.");
            }

            var versionAfter = await GetInstalledServiceToolVersionAsync();
            Console.WriteLine();
            if (versionBefore == null && versionAfter != null)
                Console.WriteLine($"  ✓ DotnetFleet.Tool installed: {versionAfter}");
            else if (versionBefore != null && versionAfter != null &&
                !string.Equals(versionBefore, versionAfter, StringComparison.Ordinal))
                Console.WriteLine($"  ✓ DotnetFleet.Tool updated: {versionBefore} → {versionAfter}");
            else if (versionAfter != null)
                Console.WriteLine($"  • DotnetFleet.Tool already up to date ({versionAfter}); nothing to update.");
            else
                Console.WriteLine("  • DotnetFleet.Tool: could not determine installed version.");
        }
        else
        {
            Console.WriteLine("  • Skipping 'dotnet tool update' (--skip-tool-update)");
        }

        if (installedServices.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  ✓ Tool updated. No services to restart.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine();
        Console.WriteLine(servicesStoppedForUpdate ? "  Starting services..." : "  Restarting services...");

        var failures = new List<string>();
        foreach (var serviceName in installedServices)
        {
            var ok = servicesStoppedForUpdate
                ? await StartServiceAsync(serviceName)
                : await RestartServiceAsync(serviceName);
            if (ok)
            {
                Console.WriteLine($"    ✓ {serviceName}");
                continue;
            }

            failures.Add(serviceName);
            Console.Error.WriteLine($"    ✗ {serviceName}");
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("  ✓ All fleet services updated and restarted.");
            Console.WriteLine();
            return;
        }

        Console.Error.WriteLine($"  ⚠ {failures.Count} service(s) failed to restart cleanly:");
        foreach (var svc in failures)
            Console.Error.WriteLine($"      sc.exe query {svc}");
        throw new InvalidOperationException("Some services failed to restart.");
    }

    private static string GetServiceToolsDir()
    {
        return GetDefaultWindowsDataDir("tools");
    }

    private static async Task<string> EnsureFleetServiceToolAsync()
    {
        var toolsDir = GetServiceToolsDir();
        Directory.CreateDirectory(toolsDir);

        var fleetPath = Path.Combine(toolsDir, "fleet.exe");
        if (File.Exists(fleetPath))
            return fleetPath;

        Console.WriteLine();
        Console.WriteLine("  • DotnetFleet.Tool service-local tool is required for Windows services.");
        Console.WriteLine($"    Installing into {toolsDir}...");
        Console.WriteLine();

        var pinnedVersion = ExtractToolVersionFromPath(Environment.ProcessPath);
        var exitCode = await RunDotnetToolPathCommandAsync("install", pinnedVersion, includePrerelease: false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to install DotnetFleet.Tool into '{toolsDir}' " +
                $"(dotnet tool install exited with code {exitCode}).");
        }

        if (!File.Exists(fleetPath))
        {
            throw new InvalidOperationException(
                $"Tool installation succeeded but '{fleetPath}' was not found.");
        }

        Console.WriteLine($"  ✓ Service tool installed at {fleetPath}");
        Console.WriteLine();
        return fleetPath;
    }

    private static async Task<int> RunDotnetToolPathCommandAsync(
        string verb,
        string? version,
        bool includePrerelease)
    {
        var toolsDir = GetServiceToolsDir();
        Directory.CreateDirectory(toolsDir);

        var args = new List<string> { "tool", verb, "--tool-path", toolsDir, "DotnetFleet.Tool" };
        if (!string.IsNullOrWhiteSpace(version))
        {
            args.Add("--version");
            args.Add(version);
        }
        if (includePrerelease)
            args.Add("--prerelease");

        var (stdout, stderr, code) = await RunProcessAsync("dotnet", args, throwOnError: false);
        if (!string.IsNullOrWhiteSpace(stdout))
            Console.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.Write(stderr);
        return code;
    }

    private static async Task<string?> GetInstalledServiceToolVersionAsync()
    {
        var toolsDir = GetServiceToolsDir();
        var (stdout, _, code) = await RunProcessAsync(
            "dotnet",
            ["tool", "list", "--tool-path", toolsDir],
            throwOnError: false);
        if (code != 0)
            return null;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                string.Equals(parts[0], "dotnetfleet.tool", StringComparison.OrdinalIgnoreCase))
                return parts[1];
        }

        return null;
    }

    private static async Task InstallServiceAsync(
        string serviceName,
        string displayName,
        string description,
        string imagePath)
    {
        if (await ServiceExistsAsync(serviceName))
        {
            await StopServiceAsync(serviceName);
            await RunScAsync(["delete", serviceName], throwOnError: false);
            await Task.Delay(1000);
        }

        await RunScAsync([
            "create", serviceName,
            "binPath=", imagePath,
            "start=", "auto",
            "DisplayName=", displayName
        ]);

        await RunScAsync(["description", serviceName, description], throwOnError: false);
        await RunScAsync([
            "failure", serviceName,
            "reset=", "60",
            "actions=", "restart/5000/restart/5000/restart/5000"
        ], throwOnError: false);
        await RunScAsync(["start", serviceName], throwOnError: false);

        await Task.Delay(1500);
        if (!await IsServiceRunningAsync(serviceName))
            Console.Error.WriteLine($"  ⚠ Service started but may not be running yet. Check: sc.exe query {serviceName}");
    }

    private static async Task UninstallServiceAsync(string serviceName)
    {
        if (!await ServiceExistsAsync(serviceName))
        {
            Console.Error.WriteLine($"  Service '{serviceName}' is not installed.");
            return;
        }

        await StopServiceAsync(serviceName);
        await RunScAsync(["delete", serviceName], throwOnError: false);
    }

    private static async Task ShowStatusAsync(string serviceName)
    {
        if (!await ServiceExistsAsync(serviceName))
        {
            Console.WriteLine($"  Service '{serviceName}' is not installed.");
            return;
        }

        var (stdout, _, _) = await RunScAsync(["query", serviceName], throwOnError: false);
        Console.WriteLine();
        Console.WriteLine(stdout.TrimEnd());
        Console.WriteLine();
        Console.WriteLine($"  Logs: use Event Viewer or the logs directory under {RootFolderName} data dir.");
        Console.WriteLine();
    }

    private static async Task<bool> ServiceExistsAsync(string serviceName)
    {
        var (_, _, code) = await RunScAsync(["query", serviceName], throwOnError: false);
        return code == 0;
    }

    private static async Task<bool> IsServiceRunningAsync(string serviceName)
    {
        var (stdout, _, code) = await RunScAsync(["query", serviceName], throwOnError: false);
        return code == 0 && stdout.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task StopServiceAsync(string serviceName)
    {
        await RunScAsync(["stop", serviceName], throwOnError: false);
        await Task.Delay(1000);
    }

    private static async Task<bool> RestartServiceAsync(string serviceName)
    {
        await StopServiceAsync(serviceName);
        return await StartServiceAsync(serviceName);
    }

    private static async Task<bool> StartServiceAsync(string serviceName)
    {
        await RunScAsync(["start", serviceName], throwOnError: false);
        await Task.Delay(1500);
        return await IsServiceRunningAsync(serviceName);
    }

    private static async Task StartServicesBestEffortAsync(IReadOnlyList<string> serviceNames)
    {
        foreach (var serviceName in serviceNames)
        {
            try { await StartServiceAsync(serviceName); }
            catch { /* best effort after failed update */ }
        }
    }

    private static async Task<List<string>> DiscoverInstalledFleetServicesAsync()
    {
        var (stdout, _, code) = await RunScAsync(["query", "state=", "all"], throwOnError: false);
        if (code != 0)
            return [];

        var services = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            const string prefix = "SERVICE_NAME:";
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var serviceName = trimmed[prefix.Length..].Trim();
            if (string.Equals(serviceName, CoordinatorServiceName, StringComparison.OrdinalIgnoreCase) ||
                serviceName.StartsWith("fleet-worker-", StringComparison.OrdinalIgnoreCase))
                services.Add(serviceName);
        }

        services.Sort(StringComparer.OrdinalIgnoreCase);
        return services;
    }

    private static string? ReadServiceImagePath(string serviceName)
    {
        try
        {
            var (stdout, _, code) = RunScAsync(["qc", serviceName], throwOnError: false)
                .GetAwaiter()
                .GetResult();
            if (code != 0)
                return null;

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                const string prefix = "BINARY_PATH_NAME";
                if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var colon = trimmed.IndexOf(':');
                if (colon >= 0 && colon + 1 < trimmed.Length)
                    return trimmed[(colon + 1)..].Trim();
            }
        }
        catch
        {
            // Best-effort discovery.
        }

        return null;
    }

    private static async Task<(string stdout, string stderr, int exitCode)> RunScAsync(
        IReadOnlyList<string> args,
        bool throwOnError = true)
    {
        return await RunProcessAsync("sc.exe", args, throwOnError);
    }

    private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        bool throwOnError = true)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (throwOnError && proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"{fileName} {string.Join(" ", args)} failed (exit {proc.ExitCode}): {stderr}");

        return (stdout, stderr, proc.ExitCode);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows service management is only supported on Windows.");
    }

    private static void EnsureAdministrator()
    {
#pragma warning disable CA1416 // Guarded by EnsureWindows before every call site.
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (principal.IsInRole(WindowsBuiltInRole.Administrator))
            return;
#pragma warning restore CA1416

        Console.Error.WriteLine("Error: This command requires an elevated Administrator terminal.");
        throw new UnauthorizedAccessException("Administrator privileges required to manage Windows services.");
    }

    private static string? ExtractToolVersionFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = path.Replace('\\', '/');
        const string marker = "/dotnetfleet.tool/";
        var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var rest = normalized[(idx + marker.Length)..];
        var slash = rest.IndexOf('/');
        if (slash <= 0)
            return null;

        var candidate = rest[..slash];
        return candidate.Length > 0 && char.IsDigit(candidate[0]) ? candidate : null;
    }
}
