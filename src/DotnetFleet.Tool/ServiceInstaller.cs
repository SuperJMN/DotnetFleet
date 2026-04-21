using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotnetFleet.Tool;

/// <summary>
/// Installs, uninstalls and queries DotnetFleet services as system-level systemd services.
/// Requires root (sudo) for install/uninstall. Services start automatically at boot.
/// </summary>
public static class ServiceInstaller
{
    private const string SystemdDir = "/etc/systemd/system";
    private const string CoordinatorServiceName = "fleet-coordinator";

    public static string WorkerServiceName(string workerName) => $"fleet-worker-{workerName}";

    // ── Platform check ───────────────────────────────────────────────────────

    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    [DllImport("libc")]
    private static extern uint geteuid();

    public static void EnsureLinux()
    {
        if (!IsLinux)
            throw new PlatformNotSupportedException(
                "Service installation is currently supported only on Linux (systemd). " +
                "On other platforms, run 'fleet coordinator' / 'fleet worker' in the foreground or use a process manager.");
    }

    public static void EnsureRoot()
    {
        if (geteuid() != 0)
        {
            Console.Error.WriteLine("Error: This command requires root privileges.");
            Console.Error.WriteLine("  Try: sudo fleet coordinator install ...");
            throw new UnauthorizedAccessException("Root privileges required to manage systemd services.");
        }
    }

    // ── Coordinator ──────────────────────────────────────────────────────────

    public record CoordinatorInstallOptions(
        int Port,
        string DataDir,
        string? Token,
        string? JwtSecret,
        string? AdminPassword,
        string? Urls);

    public static async Task InstallCoordinatorAsync(CoordinatorInstallOptions opts)
    {
        EnsureLinux();
        EnsureRoot();

        var fleetPath = ResolveFleetPath();
        var user = ResolveRunAsUser();

        Directory.CreateDirectory(opts.DataDir);

        var config = FleetConfig.LoadOrCreateCoordinatorConfig(
            opts.DataDir, opts.JwtSecret, opts.Token, opts.Port);

        // The install runs as root — fix ownership so the service user can access the data dir
        await ChownRecursiveAsync(opts.DataDir, user);

        var dotnetRoot = ResolveDotnetRoot();
        var args = BuildCoordinatorArgs(opts);
        var unit = GenerateUnit(
            description: "DotnetFleet Coordinator",
            execStart: $"{fleetPath} coordinator {args}",
            workingDirectory: opts.DataDir,
            user: user,
            dotnetRoot: dotnetRoot);

        await InstallUnitAsync(CoordinatorServiceName, unit);

        Console.WriteLine();
        Console.WriteLine($"  ✓ Coordinator installed as systemd service '{CoordinatorServiceName}'");
        Console.WriteLine($"    Listening on port {opts.Port}");
        Console.WriteLine($"    Registration token: {config.RegistrationToken}");
        Console.WriteLine($"    Data directory: {opts.DataDir}");
        Console.WriteLine();
        Console.WriteLine($"  Connect workers with:");
        Console.WriteLine($"    sudo fleet worker install --coordinator http://<host>:{opts.Port} --token {config.RegistrationToken}");
        Console.WriteLine();
        Console.WriteLine($"  Manage with:");
        Console.WriteLine($"    sudo systemctl status {CoordinatorServiceName}");
        Console.WriteLine($"    sudo systemctl stop {CoordinatorServiceName}");
        Console.WriteLine($"    sudo fleet coordinator uninstall");
        Console.WriteLine();
    }

    public static async Task UninstallCoordinatorAsync()
    {
        EnsureLinux();
        EnsureRoot();
        await UninstallUnitAsync(CoordinatorServiceName);
        Console.WriteLine($"  ✓ Coordinator service '{CoordinatorServiceName}' removed.");
    }

    public static async Task CoordinatorStatusAsync()
    {
        EnsureLinux();
        await ShowStatusAsync(CoordinatorServiceName);
    }

    // ── Worker ───────────────────────────────────────────────────────────────

    public record WorkerInstallOptions(
        string CoordinatorUrl,
        string? Token,
        string Name,
        string DataDir,
        int? PollInterval,
        double? MaxDisk);

    public static async Task InstallWorkerAsync(WorkerInstallOptions opts)
    {
        EnsureLinux();
        EnsureRoot();

        var fleetPath = ResolveFleetPath();
        var user = ResolveRunAsUser();
        var serviceName = WorkerServiceName(opts.Name);

        Directory.CreateDirectory(opts.DataDir);

        var dotnetRoot = ResolveDotnetRoot();
        var args = BuildWorkerArgs(opts);
        var unit = GenerateUnit(
            description: $"DotnetFleet Worker ({opts.Name})",
            execStart: $"{fleetPath} worker {args}",
            workingDirectory: opts.DataDir,
            user: user,
            dotnetRoot: dotnetRoot);

        await ChownRecursiveAsync(opts.DataDir, user);
        await InstallUnitAsync(serviceName, unit);

        Console.WriteLine();
        Console.WriteLine($"  ✓ Worker '{opts.Name}' installed as systemd service '{serviceName}'");
        Console.WriteLine($"    Coordinator: {opts.CoordinatorUrl}");
        Console.WriteLine($"    Data directory: {opts.DataDir}");
        Console.WriteLine();
        Console.WriteLine($"  Manage with:");
        Console.WriteLine($"    sudo systemctl status {serviceName}");
        Console.WriteLine($"    sudo systemctl stop {serviceName}");
        Console.WriteLine($"    sudo fleet worker uninstall --name {opts.Name}");
        Console.WriteLine();
    }

    public static async Task UninstallWorkerAsync(string workerName)
    {
        EnsureLinux();
        EnsureRoot();
        var serviceName = WorkerServiceName(workerName);
        await UninstallUnitAsync(serviceName);
        Console.WriteLine($"  ✓ Worker service '{serviceName}' removed.");
    }

    public static async Task WorkerStatusAsync(string workerName)
    {
        EnsureLinux();
        await ShowStatusAsync(WorkerServiceName(workerName));
    }

    // ── Systemd helpers ──────────────────────────────────────────────────────

    private static string GenerateUnit(string description, string execStart, string workingDirectory, string user, string? dotnetRoot)
    {
        var dotnetRootLine = dotnetRoot != null
            ? $"\nEnvironment=DOTNET_ROOT={dotnetRoot}"
            : "";

        return $"""
            [Unit]
            Description={description}
            After=network.target

            [Service]
            Type=exec
            ExecStart={execStart}
            WorkingDirectory={workingDirectory}
            Restart=on-failure
            RestartSec=5
            User={user}
            Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
            Environment=DOTNET_NOLOGO=1{dotnetRootLine}

            [Install]
            WantedBy=multi-user.target
            """;
    }

    private static async Task InstallUnitAsync(string serviceName, string unitContent)
    {
        var unitPath = Path.Combine(SystemdDir, $"{serviceName}.service");

        // Stop existing if running
        if (File.Exists(unitPath))
        {
            await RunSystemctlAsync("stop", serviceName, throwOnError: false);
            await RunSystemctlAsync("disable", serviceName, throwOnError: false);
        }

        await File.WriteAllTextAsync(unitPath, unitContent);
        await RunSystemctlAsync("daemon-reload");
        await RunSystemctlAsync("enable", serviceName);
        await RunSystemctlAsync("start", serviceName);

        // Brief wait then check status
        await Task.Delay(1500);
        var (active, _) = await RunSystemctlAsync("is-active", serviceName, throwOnError: false);
        if (active.Trim() != "active")
        {
            Console.Error.WriteLine($"  ⚠ Service started but may not be active yet. Check: systemctl status {serviceName}");
        }
    }

    private static async Task UninstallUnitAsync(string serviceName)
    {
        var unitPath = Path.Combine(SystemdDir, $"{serviceName}.service");

        if (!File.Exists(unitPath))
        {
            Console.Error.WriteLine($"  Service '{serviceName}' is not installed.");
            return;
        }

        await RunSystemctlAsync("stop", serviceName, throwOnError: false);
        await RunSystemctlAsync("disable", serviceName, throwOnError: false);
        File.Delete(unitPath);
        await RunSystemctlAsync("daemon-reload");
    }

    private static async Task ShowStatusAsync(string serviceName)
    {
        var unitPath = Path.Combine(SystemdDir, $"{serviceName}.service");

        if (!File.Exists(unitPath))
        {
            Console.WriteLine($"  Service '{serviceName}' is not installed.");
            return;
        }

        var (active, _) = await RunSystemctlAsync("is-active", serviceName, throwOnError: false);
        var status = active.Trim();

        Console.WriteLine();
        Console.WriteLine($"  Service: {serviceName}");
        Console.WriteLine($"  Status:  {status}");

        if (status == "active")
        {
            var (details, _) = await RunSystemctlAsync(
                "show", serviceName,
                "--property=MainPID,MemoryCurrent,ActiveEnterTimestamp",
                throwOnError: false);

            foreach (var line in details.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                {
                    var label = parts[0] switch
                    {
                        "MainPID" => "PID",
                        "MemoryCurrent" => "Memory",
                        "ActiveEnterTimestamp" => "Running since",
                        _ => parts[0]
                    };
                    var value = parts[0] == "MemoryCurrent"
                        ? FormatBytes(long.TryParse(parts[1], out var bytes) ? bytes : 0)
                        : parts[1];
                    Console.WriteLine($"  {label,-14} {value}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  Logs: journalctl -u {serviceName} -f");
        Console.WriteLine();
    }

    // ── Argument builders ────────────────────────────────────────────────────

    private static string BuildCoordinatorArgs(CoordinatorInstallOptions opts)
    {
        var parts = new List<string>
        {
            $"--port {opts.Port}",
            $"--data-dir \"{opts.DataDir}\""
        };
        if (opts.Token != null) parts.Add($"--token \"{opts.Token}\"");
        if (opts.JwtSecret != null) parts.Add($"--jwt-secret \"{opts.JwtSecret}\"");
        if (opts.AdminPassword != null) parts.Add($"--admin-password \"{opts.AdminPassword}\"");
        if (opts.Urls != null) parts.Add($"--urls \"{opts.Urls}\"");
        return string.Join(" ", parts);
    }

    private static string BuildWorkerArgs(WorkerInstallOptions opts)
    {
        var parts = new List<string>
        {
            $"--coordinator \"{opts.CoordinatorUrl}\"",
            $"--name \"{opts.Name}\"",
            $"--data-dir \"{opts.DataDir}\""
        };
        if (opts.Token != null) parts.Add($"--token \"{opts.Token}\"");
        if (opts.PollInterval.HasValue) parts.Add($"--poll-interval {opts.PollInterval}");
        if (opts.MaxDisk.HasValue) parts.Add($"--max-disk {opts.MaxDisk}");
        return string.Join(" ", parts);
    }

    // ── Process helpers ──────────────────────────────────────────────────────

    private static string ResolveFleetPath()
    {
        var currentExe = Environment.ProcessPath;
        if (currentExe != null && File.Exists(currentExe))
            return currentExe;

        var (whichResult, whichCode) = RunProcessSync("which", "fleet");
        if (whichCode == 0 && !string.IsNullOrWhiteSpace(whichResult))
            return whichResult.Trim();

        throw new InvalidOperationException(
            "Cannot determine the path to the 'fleet' executable. " +
            "Ensure it is installed as a dotnet global tool: dotnet tool install -g DotnetFleet.Tool");
    }

    private static string ResolveRunAsUser()
    {
        return Environment.GetEnvironmentVariable("SUDO_USER")
               ?? Environment.UserName;
    }

    private static string? ResolveDotnetRoot()
    {
        // 1. Explicit env var (e.g. passed via sudo DOTNET_ROOT=...)
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (root != null && Directory.Exists(root))
            return root;

        // 2. Infer from the running dotnet process location
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null)
        {
            // Runtime is at <DOTNET_ROOT>/shared/Microsoft.NETCore.App/<version>/
            var candidate = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
            if (File.Exists(Path.Combine(candidate, "dotnet")))
                return candidate;
        }

        // 3. Check common user install location for the target user
        var user = Environment.GetEnvironmentVariable("SUDO_USER");
        if (user != null)
        {
            var home = FleetConfig.GetDefaultDataDir("").Replace("/.fleet/", "");
            var userDotnet = Path.Combine(home, ".dotnet");
            if (File.Exists(Path.Combine(userDotnet, "dotnet")))
                return userDotnet;
        }

        return null;
    }

    private static async Task<(string stdout, int exitCode)> RunSystemctlAsync(
        string command, string? serviceName = null, string? extraArgs = null, bool throwOnError = true)
    {
        var arguments = serviceName != null ? $"{command} {serviceName}" : command;
        if (extraArgs != null) arguments += $" {extraArgs}";

        var psi = new ProcessStartInfo("systemctl", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (throwOnError && proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"systemctl {arguments} failed (exit {proc.ExitCode}): {stderr}");
        }

        return (stdout, proc.ExitCode);
    }

    private static (string stdout, int exitCode) RunProcessSync(string command, string arguments)
    {
        var psi = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return (stdout, proc.ExitCode);
        }
        catch
        {
            return ("", 1);
        }
    }

    private static async Task ChownRecursiveAsync(string path, string user)
    {
        var psi = new ProcessStartInfo("chown", $"-R {user}:{user} \"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
        }
        catch
        {
            // Best-effort
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
