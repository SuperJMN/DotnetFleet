using System.CommandLine;
using DotnetFleet.Coordinator;
using DotnetFleet.Tool;
using DotnetFleet.WorkerService;
using Microsoft.Extensions.Hosting;
using Serilog;

var rootCommand = new RootCommand("DotnetFleet — self-hosted CI/CD for .NET projects");

// ── fleet coordinator ────────────────────────────────────────────────────────

var coordinatorCommand = new Command("coordinator", "Start the DotnetFleet coordinator (API server)");

var portOption = new Option<int>("--port", "-p")
{
    Description = "HTTP port to listen on",
    DefaultValueFactory = _ => 5000
};
var coordDataDirOption = new Option<string?>("--data-dir")
{
    Description = "Data directory (default: ~/.fleet/coordinator)"
};
var tokenOption = new Option<string?>("--token", "-t")
{
    Description = "Worker registration token (auto-generated if omitted)"
};
var jwtSecretOption = new Option<string?>("--jwt-secret")
{
    Description = "JWT signing secret (auto-generated if omitted)"
};
var adminPasswordOption = new Option<string?>("--admin-password")
{
    Description = "Admin user password (default: admin)"
};
var urlsOption = new Option<string?>("--urls")
{
    Description = "ASP.NET Core URLs override (e.g. http://0.0.0.0:5000)"
};
var noMdnsOption = new Option<bool>("--no-mdns")
{
    Description = "Disable mDNS advertising (workers won't auto-discover this coordinator on the LAN)"
};

coordinatorCommand.Options.Add(portOption);
coordinatorCommand.Options.Add(coordDataDirOption);
coordinatorCommand.Options.Add(tokenOption);
coordinatorCommand.Options.Add(jwtSecretOption);
coordinatorCommand.Options.Add(adminPasswordOption);
coordinatorCommand.Options.Add(urlsOption);
coordinatorCommand.Options.Add(noMdnsOption);

// Default action: run coordinator in foreground
coordinatorCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var port = parseResult.GetValue(portOption);
    var dataDir = parseResult.GetValue(coordDataDirOption) ?? FleetConfig.GetDefaultDataDir("coordinator");
    var jwtSecret = parseResult.GetValue(jwtSecretOption);
    var regToken = parseResult.GetValue(tokenOption);
    var adminPassword = parseResult.GetValue(adminPasswordOption);
    var urls = parseResult.GetValue(urlsOption);
    var noMdns = parseResult.GetValue(noMdnsOption);

    var config = FleetConfig.LoadOrCreateCoordinatorConfig(dataDir, jwtSecret, regToken, port);

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    PrintCoordinatorBanner(port, config.RegistrationToken, adminPassword ?? "admin", urls);

    try
    {
        var options = new CoordinatorStartupOptions
        {
            Port = port,
            JwtSecret = config.JwtSecret,
            RegistrationToken = config.RegistrationToken,
            DataDir = dataDir,
            AdminPassword = adminPassword,
            Urls = urls,
            NoMdns = noMdns
        };

        var app = CoordinatorHostBuilder.Build(options, []);
        await CoordinatorHostBuilder.InitializeDatabaseAsync(app);
        await HostingAbstractionsHostExtensions.RunAsync(app, cancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Log.Fatal(ex, "Coordinator terminated unexpectedly");
        return 1;
    }
    finally
    {
        await Log.CloseAndFlushAsync();
    }

    return 0;
});

// ── fleet coordinator install ────────────────────────────────────────────
var coordInstallCommand = new Command("install", "Install coordinator as a systemd service");
coordInstallCommand.Options.Add(portOption);
coordInstallCommand.Options.Add(coordDataDirOption);
coordInstallCommand.Options.Add(tokenOption);
coordInstallCommand.Options.Add(jwtSecretOption);
coordInstallCommand.Options.Add(adminPasswordOption);
coordInstallCommand.Options.Add(urlsOption);
coordInstallCommand.Options.Add(noMdnsOption);

coordInstallCommand.SetAction(async (parseResult, _) =>
{
    var elevated = SudoElevation.ReExecAsRootIfNeeded();
    if (elevated.HasValue) Environment.Exit(elevated.Value);

    var port = parseResult.GetValue(portOption);
    var dataDir = parseResult.GetValue(coordDataDirOption) ?? FleetConfig.GetDefaultDataDir("coordinator");
    await ServiceInstaller.InstallCoordinatorAsync(new ServiceInstaller.CoordinatorInstallOptions(
        Port: port,
        DataDir: dataDir,
        Token: parseResult.GetValue(tokenOption),
        JwtSecret: parseResult.GetValue(jwtSecretOption),
        AdminPassword: parseResult.GetValue(adminPasswordOption),
        Urls: parseResult.GetValue(urlsOption),
        NoMdns: parseResult.GetValue(noMdnsOption)));
});

// ── fleet coordinator uninstall ──────────────────────────────────────────
var coordUninstallCommand = new Command("uninstall", "Remove the coordinator systemd service");
coordUninstallCommand.SetAction(async (_, _) =>
{
    var elevated = SudoElevation.ReExecAsRootIfNeeded();
    if (elevated.HasValue) Environment.Exit(elevated.Value);

    await ServiceInstaller.UninstallCoordinatorAsync();
});

// ── fleet coordinator status ─────────────────────────────────────────────
var coordStatusCommand = new Command("status", "Show coordinator service status");
coordStatusCommand.SetAction(async (_, _) =>
{
    await ServiceInstaller.CoordinatorStatusAsync();
});

coordinatorCommand.Subcommands.Add(coordInstallCommand);
coordinatorCommand.Subcommands.Add(coordUninstallCommand);
coordinatorCommand.Subcommands.Add(coordStatusCommand);

// ── fleet worker ─────────────────────────────────────────────────────────────

var workerCommand = new Command("worker", "Start a DotnetFleet worker that connects to a coordinator");

var coordinatorUrlOption = new Option<string?>("--coordinator", "-c")
{
    Description = "Coordinator URL (e.g. http://myserver:5000). Auto-discovered on the local machine and via mDNS on the LAN if omitted."
};
var workerTokenOption = new Option<string?>("--token", "-t")
{
    Description = "Registration token (required on first run; auto-discovered when the coordinator runs on this machine)"
};
var nameOption = new Option<string?>("--name", "-n")
{
    Description = "Worker display name (default: hostname)"
};
var workerDataDirOption = new Option<string?>("--data-dir")
{
    Description = "Data directory (default: ~/.fleet/worker-{name})"
};
var pollIntervalOption = new Option<int?>("--poll-interval")
{
    Description = "Queue polling interval in seconds (default: 10)"
};
var maxDiskOption = new Option<double?>("--max-disk")
{
    Description = "Max disk usage in GB for repo cache (default: 10)"
};
var noDiscoverOption = new Option<bool>("--no-discover")
{
    Description = "Disable auto-discovery (local + mDNS) of the coordinator"
};

workerCommand.Options.Add(coordinatorUrlOption);
workerCommand.Options.Add(workerTokenOption);
workerCommand.Options.Add(nameOption);
workerCommand.Options.Add(workerDataDirOption);
workerCommand.Options.Add(pollIntervalOption);
workerCommand.Options.Add(maxDiskOption);
workerCommand.Options.Add(noDiscoverOption);

// Default action: run worker in foreground
workerCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var explicitUrl = parseResult.GetValue(coordinatorUrlOption);
    var explicitToken = parseResult.GetValue(workerTokenOption);
    var noDiscover = parseResult.GetValue(noDiscoverOption);
    var workerName = parseResult.GetValue(nameOption) ?? Environment.MachineName;
    var dataDir = parseResult.GetValue(workerDataDirOption)
                  ?? FleetConfig.GetDefaultDataDir($"worker-{workerName}");
    var pollInterval = parseResult.GetValue(pollIntervalOption);
    var maxDisk = parseResult.GetValue(maxDiskOption);

    var resolved = await CoordinatorResolver.ResolveAsync(explicitUrl, explicitToken, noDiscover);
    if (resolved == null) return 1;

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    Console.WriteLine();
    Console.WriteLine($"  DotnetFleet Worker \"{workerName}\"");
    Console.WriteLine($"  Coordinator: {resolved.Url}");
    Console.WriteLine($"  Data dir:    {dataDir}");
    Console.WriteLine();

    try
    {
        var options = new WorkerStartupOptions
        {
            CoordinatorUrl = resolved.Url,
            RegistrationToken = resolved.Token,
            Name = workerName,
            DataDir = dataDir,
            PollIntervalSeconds = pollInterval,
            MaxDiskGb = maxDisk
        };

        var host = await WorkerHostBuilder.BuildAsync(options, []);
        await host.RunAsync(cancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Log.Fatal(ex, "Worker terminated unexpectedly");
        return 1;
    }
    finally
    {
        await Log.CloseAndFlushAsync();
    }

    return 0;
});

// ── fleet worker install ─────────────────────────────────────────────────
var workerInstallCommand = new Command("install", "Install worker as a systemd service");
workerInstallCommand.Options.Add(coordinatorUrlOption);
workerInstallCommand.Options.Add(workerTokenOption);
workerInstallCommand.Options.Add(nameOption);
workerInstallCommand.Options.Add(workerDataDirOption);
workerInstallCommand.Options.Add(pollIntervalOption);
workerInstallCommand.Options.Add(maxDiskOption);
workerInstallCommand.Options.Add(noDiscoverOption);

workerInstallCommand.SetAction(async (parseResult, _) =>
{
    var elevated = SudoElevation.ReExecAsRootIfNeeded();
    if (elevated.HasValue) return elevated.Value;

    var workerName = parseResult.GetValue(nameOption) ?? Environment.MachineName;
    var dataDir = parseResult.GetValue(workerDataDirOption)
                  ?? FleetConfig.GetDefaultDataDir($"worker-{workerName}");
    var explicitUrl = parseResult.GetValue(coordinatorUrlOption);
    var explicitToken = parseResult.GetValue(workerTokenOption);
    var noDiscover = parseResult.GetValue(noDiscoverOption);

    var resolved = await CoordinatorResolver.ResolveAsync(explicitUrl, explicitToken, noDiscover);
    if (resolved == null) return 1;

    await ServiceInstaller.InstallWorkerAsync(new ServiceInstaller.WorkerInstallOptions(
        CoordinatorUrl: resolved.Url,
        Token: resolved.Token,
        Name: workerName,
        DataDir: dataDir,
        PollInterval: parseResult.GetValue(pollIntervalOption),
        MaxDisk: parseResult.GetValue(maxDiskOption)));
    return 0;
});

// ── fleet worker uninstall ───────────────────────────────────────────────
var workerUninstallCommand = new Command("uninstall", "Remove a worker systemd service");
var uninstallNameOption = new Option<string?>("--name", "-n")
{
    Description = "Worker name to uninstall (default: hostname)"
};
workerUninstallCommand.Options.Add(uninstallNameOption);
workerUninstallCommand.SetAction(async (parseResult, _) =>
{
    var elevated = SudoElevation.ReExecAsRootIfNeeded();
    if (elevated.HasValue) Environment.Exit(elevated.Value);

    var workerName = parseResult.GetValue(uninstallNameOption) ?? Environment.MachineName;
    await ServiceInstaller.UninstallWorkerAsync(workerName);
});

// ── fleet worker status ──────────────────────────────────────────────────
var workerStatusCommand = new Command("status", "Show worker service status");
var statusNameOption = new Option<string?>("--name", "-n")
{
    Description = "Worker name to check (default: hostname)"
};
workerStatusCommand.Options.Add(statusNameOption);
workerStatusCommand.SetAction(async (parseResult, _) =>
{
    var workerName = parseResult.GetValue(statusNameOption) ?? Environment.MachineName;
    await ServiceInstaller.WorkerStatusAsync(workerName);
});

workerCommand.Subcommands.Add(workerInstallCommand);
workerCommand.Subcommands.Add(workerUninstallCommand);
workerCommand.Subcommands.Add(workerStatusCommand);

// ── fleet update ─────────────────────────────────────────────────────────────

var updateCommand = new Command("update", "Update DotnetFleet.Tool and restart local coordinator/worker services");

var skipToolUpdateOption = new Option<bool>("--skip-tool-update")
{
    Description = "Only restart installed services; skip 'dotnet tool update'"
};
var updateVersionOption = new Option<string?>("--version")
{
    Description = "Pin a specific DotnetFleet.Tool version (default: latest)"
};
var updatePrereleaseOption = new Option<bool>("--prerelease")
{
    Description = "Allow prerelease versions when updating the tool"
};

updateCommand.Options.Add(skipToolUpdateOption);
updateCommand.Options.Add(updateVersionOption);
updateCommand.Options.Add(updatePrereleaseOption);

updateCommand.SetAction(async (parseResult, _) =>
{
    var elevated = SudoElevation.ReExecAsRootIfNeeded();
    if (elevated.HasValue) return elevated.Value;

    try
    {
        await ServiceInstaller.UpdateLocalServicesAsync(new ServiceInstaller.UpdateOptions(
            SkipToolUpdate: parseResult.GetValue(skipToolUpdateOption),
            Version: parseResult.GetValue(updateVersionOption),
            IncludePrerelease: parseResult.GetValue(updatePrereleaseOption)));
        return 0;
    }
    catch (Exception)
    {
        return 1;
    }
});

// ── fleet version ────────────────────────────────────────────────────────────

var versionCommand = new Command("version", "Show DotnetFleet version");
versionCommand.SetAction(_ =>
{
    var version = typeof(FleetConfig).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    Console.WriteLine($"DotnetFleet {version}");
});

// ── Root ─────────────────────────────────────────────────────────────────────

rootCommand.Subcommands.Add(coordinatorCommand);
rootCommand.Subcommands.Add(workerCommand);
rootCommand.Subcommands.Add(updateCommand);
rootCommand.Subcommands.Add(versionCommand);

return rootCommand.Parse(args).Invoke();

// ── Helpers ──────────────────────────────────────────────────────────────────

static void PrintCoordinatorBanner(int port, string registrationToken, string adminPassword, string? urls)
{
    var listenUrl = urls ?? $"http://0.0.0.0:{port}";

    Console.WriteLine();
    Console.WriteLine("  ╔══════════════════════════════════════════════════════════════╗");
    Console.WriteLine("  ║                   DotnetFleet Coordinator                    ║");
    Console.WriteLine("  ╠══════════════════════════════════════════════════════════════╣");
    Console.WriteLine($"  ║  Listening on:       {Truncate(listenUrl, 37),-37} ║");
    Console.WriteLine($"  ║  Admin credentials:  admin / {Truncate(adminPassword, 28),-28} ║");
    Console.WriteLine($"  ║  Registration token: {Truncate(registrationToken, 37),-37} ║");
    Console.WriteLine("  ╠══════════════════════════════════════════════════════════════╣");
    Console.WriteLine("  ║  Connect workers with:                                      ║");
    Console.WriteLine($"  ║    fleet worker --coordinator {Truncate(listenUrl, 28),-28} \\  ║");
    Console.WriteLine($"  ║                --token {Truncate(registrationToken, 34),-34} \\  ║");
    Console.WriteLine("  ╚══════════════════════════════════════════════════════════════╝");
    Console.WriteLine();
}

static string Truncate(string value, int maxLength) =>
    value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
