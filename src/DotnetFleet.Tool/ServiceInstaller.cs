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
        string? Urls,
        bool NoMdns = false);

    public static async Task InstallCoordinatorAsync(CoordinatorInstallOptions opts)
    {
        EnsureLinux();
        EnsureRoot();

        var user = ResolveRunAsUser();
        var fleetPath = await EnsureFleetGlobalToolAsync(user);

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

        // Show config info (token, data dir) by reading the WorkingDirectory from the unit
        var dataDir = ReadWorkingDirectoryFromUnit(CoordinatorServiceName);
        if (dataDir != null)
        {
            var configPath = Path.Combine(dataDir, "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    var config = FleetConfig.LoadOrCreateCoordinatorConfig(dataDir, null, null, 5000);
                    Console.WriteLine($"  Token: {config.RegistrationToken}");
                    Console.WriteLine($"  Data:  {dataDir}");
                    Console.WriteLine();
                }
                catch { }
            }
        }
    }

    private static string? ReadWorkingDirectoryFromUnit(string serviceName)
    {
        var unitPath = Path.Combine(SystemdDir, $"{serviceName}.service");
        if (!File.Exists(unitPath)) return null;

        foreach (var line in File.ReadLines(unitPath))
        {
            if (line.StartsWith("WorkingDirectory="))
                return line["WorkingDirectory=".Length..].Trim();
        }

        return null;
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

        var user = ResolveRunAsUser();
        var fleetPath = await EnsureFleetGlobalToolAsync(user);
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

    // ── Update ───────────────────────────────────────────────────────────────

    public record UpdateOptions(bool SkipToolUpdate, string? Version, bool IncludePrerelease);

    /// <summary>
    /// Updates the DotnetFleet.Tool global tool and restarts any locally installed
    /// fleet-coordinator / fleet-worker-* systemd services so they pick up the new binary.
    /// </summary>
    public static async Task UpdateLocalServicesAsync(UpdateOptions opts)
    {
        EnsureLinux();
        EnsureRoot();

        var installedServices = DiscoverInstalledFleetServices();

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

        if (!opts.SkipToolUpdate)
        {
            var exitCode = await RunDotnetToolUpdateAsync(opts.Version, opts.IncludePrerelease);
            if (exitCode != 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("  ✗ 'dotnet tool update' failed. Services were not restarted.");
                Console.Error.WriteLine("    Re-run with --skip-tool-update to only restart services.");
                throw new InvalidOperationException("dotnet tool update failed.");
            }
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
        Console.WriteLine("  Restarting services...");

        var failures = new List<string>();
        foreach (var svc in installedServices)
        {
            var (_, code) = await RunSystemctlAsync("restart", svc, throwOnError: false);
            if (code != 0)
            {
                failures.Add(svc);
                Console.Error.WriteLine($"    ✗ {svc} (systemctl restart exit {code})");
                continue;
            }

            await Task.Delay(800);
            var (active, _) = await RunSystemctlAsync("is-active", svc, throwOnError: false);
            var status = active.Trim();
            if (status == "active")
                Console.WriteLine($"    ✓ {svc}");
            else
            {
                failures.Add(svc);
                Console.Error.WriteLine($"    ✗ {svc} (status: {status})");
            }
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("  ✓ All fleet services updated and restarted.");
        }
        else
        {
            Console.Error.WriteLine($"  ⚠ {failures.Count} service(s) failed to restart cleanly:");
            foreach (var svc in failures)
                Console.Error.WriteLine($"      journalctl -u {svc} -n 50 --no-pager");
            throw new InvalidOperationException("Some services failed to restart.");
        }
        Console.WriteLine();
    }

    private static List<string> DiscoverInstalledFleetServices()
    {
        var services = new List<string>();
        if (!Directory.Exists(SystemdDir))
            return services;

        var coordinatorUnit = Path.Combine(SystemdDir, $"{CoordinatorServiceName}.service");
        if (File.Exists(coordinatorUnit))
            services.Add(CoordinatorServiceName);

        foreach (var path in Directory.EnumerateFiles(SystemdDir, "fleet-worker-*.service"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(name))
                services.Add(name);
        }

        return services;
    }

    private static async Task<int> RunDotnetToolUpdateAsync(string? version, bool includePrerelease)
    {
        return await RunDotnetToolCommandAsync("update", version, includePrerelease);
    }

    private static async Task<int> RunDotnetToolCommandAsync(string verb, string? version, bool includePrerelease)
    {
        var runAsUser = ResolveRunAsUser();
        var runningAsRoot = geteuid() == 0;
        var crossUser = runningAsRoot && !string.Equals(runAsUser, "root", StringComparison.Ordinal);

        var (dotnetBinary, dotnetRoot) = ResolveDotnetForUser(crossUser ? runAsUser : null);
        if (dotnetBinary == null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("  ✗ Could not locate a 'dotnet' executable.");
            Console.Error.WriteLine("    Set DOTNET_ROOT or install the .NET SDK for the target user.");
            return 1;
        }

        var toolArgs = new List<string> { "tool", verb, "-g", "DotnetFleet.Tool" };
        if (!string.IsNullOrWhiteSpace(version))
        {
            toolArgs.Add("--version");
            toolArgs.Add(version);
        }
        if (includePrerelease)
            toolArgs.Add("--prerelease");

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = false
        };

        if (crossUser)
        {
            // sudo strips PATH/DOTNET_ROOT, so we hand them to the child via `env`
            // and invoke dotnet by absolute path to be safe.
            var userHome = ResolveUserHome(runAsUser);
            var pathForUser = BuildPathForUser(dotnetRoot, userHome);

            psi.FileName = "sudo";
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(runAsUser);
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add("env");
            if (dotnetRoot != null)
                psi.ArgumentList.Add($"DOTNET_ROOT={dotnetRoot}");
            psi.ArgumentList.Add($"PATH={pathForUser}");
            if (userHome != null)
                psi.ArgumentList.Add($"HOME={userHome}");
            psi.ArgumentList.Add(dotnetBinary);
            foreach (var a in toolArgs) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = dotnetBinary;
            foreach (var a in toolArgs) psi.ArgumentList.Add(a);
            if (dotnetRoot != null)
            {
                psi.Environment["DOTNET_ROOT"] = dotnetRoot;
                var path = Environment.GetEnvironmentVariable("PATH") ?? DefaultSystemPath;
                if (!path.Split(':').Contains(dotnetRoot))
                    psi.Environment["PATH"] = $"{dotnetRoot}:{path}";
            }
        }

        Console.WriteLine($"  • Running: {psi.FileName} {string.Join(" ", psi.ArgumentList)}");
        Console.WriteLine();

        try
        {
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ✗ Failed to invoke '{psi.FileName}': {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Returns (absolute path to dotnet binary, DOTNET_ROOT) preferring the target user's
    /// install (~/.dotnet/dotnet) when invoking across users via sudo.
    /// </summary>
    private static (string? binary, string? root) ResolveDotnetForUser(string? targetUser)
    {
        // Prefer the target user's per-user install (where their global tools live)
        if (targetUser != null)
        {
            var userHome = ResolveUserHome(targetUser);
            if (userHome != null)
            {
                var userDotnet = Path.Combine(userHome, ".dotnet");
                var userBin = Path.Combine(userDotnet, "dotnet");
                if (File.Exists(userBin))
                    return (userBin, userDotnet);
            }
        }

        var root = ResolveDotnetRoot();
        if (root != null)
        {
            var bin = Path.Combine(root, "dotnet");
            if (File.Exists(bin))
                return (bin, root);
        }

        // Fall back to PATH lookup
        var (which, code) = RunProcessSync("which", "dotnet");
        if (code == 0 && !string.IsNullOrWhiteSpace(which))
        {
            var bin = which.Trim();
            return (bin, Path.GetDirectoryName(bin));
        }

        return (null, null);
    }

    private static string? ResolveUserHome(string user)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var fields = line.Split(':');
                if (fields.Length >= 6 && fields[0] == user)
                    return fields[5];
            }
        }
        catch { }
        return null;
    }

    private static string BuildPathForUser(string? dotnetRoot, string? userHome)
    {
        var parts = new List<string>();
        if (dotnetRoot != null) parts.Add(dotnetRoot);
        if (userHome != null)
        {
            parts.Add(Path.Combine(userHome, ".dotnet"));
            parts.Add(Path.Combine(userHome, ".dotnet", "tools"));
        }
        parts.Add(DefaultSystemPath);
        return string.Join(":", parts);
    }

    // ── Systemd helpers ──────────────────────────────────────────────────────

    private const string DefaultSystemPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    private static string GenerateUnit(string description, string execStart, string workingDirectory, string user, string? dotnetRoot)
    {
        var userHome = ResolveUserHome(user);
        var path = BuildPathForUser(dotnetRoot, userHome);

        var envLines = new List<string>
        {
            "Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "Environment=DOTNET_NOLOGO=1",
            $"Environment=PATH={path}",
        };

        if (dotnetRoot != null)
            envLines.Add($"Environment=DOTNET_ROOT={dotnetRoot}");

        if (userHome != null)
        {
            envLines.Add($"Environment=HOME={userHome}");
            envLines.Add($"Environment=DOTNET_TOOLS_ROOT={Path.Combine(userHome, ".dotnet", "tools")}");
        }

        var environmentBlock = string.Join("\n", envLines);

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
            {environmentBlock}

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
        if (opts.NoMdns) parts.Add("--no-mdns");
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

    /// <summary>
    /// Returns the absolute path to the 'fleet' global tool for <paramref name="targetUser"/>,
    /// auto-installing the DotnetFleet.Tool global tool if we're currently running from an
    /// ephemeral location (e.g. dnx / NuGet package cache). systemd needs a stable ExecStart path.
    /// </summary>
    private static async Task<string> EnsureFleetGlobalToolAsync(string targetUser)
    {
        var userHome = ResolveUserHome(targetUser)
                       ?? throw new InvalidOperationException($"Could not resolve home directory for user '{targetUser}'.");
        var globalToolPath = Path.Combine(userHome, ".dotnet", "tools", "fleet");
        var currentExe = Environment.ProcessPath;
        var ephemeral = currentExe != null && IsEphemeralPath(currentExe);

        if (!ephemeral && currentExe != null && File.Exists(currentExe))
        {
            // Already running from a stable path (likely the global tool itself).
            return currentExe;
        }

        if (File.Exists(globalToolPath) && !ephemeral)
            return globalToolPath;

        // Need to install (or reinstall to refresh, when running ephemerally).
        var pinnedVersion = ephemeral ? ExtractToolVersionFromPath(currentExe!) : null;
        var verb = File.Exists(globalToolPath) ? "update" : "install";

        Console.WriteLine();
        Console.WriteLine($"  • DotnetFleet.Tool global tool is required for systemd services.");
        if (ephemeral)
            Console.WriteLine($"    Detected ephemeral execution path: {currentExe}");
        Console.WriteLine($"    Running 'dotnet tool {verb} -g DotnetFleet.Tool{(pinnedVersion != null ? $" --version {pinnedVersion}" : "")}' for user '{targetUser}'...");
        Console.WriteLine();

        var exitCode = await RunDotnetToolCommandAsync(verb, pinnedVersion, includePrerelease: false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to install DotnetFleet.Tool global tool for user '{targetUser}' " +
                $"(dotnet tool {verb} exited with code {exitCode}). " +
                "Install it manually with: dotnet tool install -g DotnetFleet.Tool");
        }

        if (!File.Exists(globalToolPath))
        {
            throw new InvalidOperationException(
                $"Global tool installation succeeded but '{globalToolPath}' was not found. " +
                "Check the user's ~/.dotnet/tools directory.");
        }

        Console.WriteLine($"  ✓ Global tool installed at {globalToolPath}");
        Console.WriteLine();
        return globalToolPath;
    }

    private static bool IsEphemeralPath(string path)
    {
        return path.Contains("/.nuget/packages/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/.dotnet/toolResolverCache/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/dotnetCliToolResolver/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractToolVersionFromPath(string path)
    {
        // dnx / NuGet layout: .../packages/dotnetfleet.tool/<version>/tools/<tfm>/<rid>/...
        const string marker = "/dotnetfleet.tool/";
        var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = path[(idx + marker.Length)..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return null;
        var candidate = rest[..slash];
        // Sanity check: looks like a SemVer-ish version (starts with a digit).
        return candidate.Length > 0 && char.IsDigit(candidate[0]) ? candidate : null;
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
