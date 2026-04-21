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

coordinatorCommand.Options.Add(portOption);
coordinatorCommand.Options.Add(coordDataDirOption);
coordinatorCommand.Options.Add(tokenOption);
coordinatorCommand.Options.Add(jwtSecretOption);
coordinatorCommand.Options.Add(adminPasswordOption);
coordinatorCommand.Options.Add(urlsOption);

coordinatorCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var port = parseResult.GetValue(portOption);
    var dataDir = parseResult.GetValue(coordDataDirOption) ?? FleetConfig.GetDefaultDataDir("coordinator");
    var jwtSecret = parseResult.GetValue(jwtSecretOption);
    var regToken = parseResult.GetValue(tokenOption);
    var adminPassword = parseResult.GetValue(adminPasswordOption);
    var urls = parseResult.GetValue(urlsOption);

    var config = FleetConfig.LoadOrCreateCoordinatorConfig(dataDir, jwtSecret, regToken, port);

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
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
            Urls = urls
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

// ── fleet worker ─────────────────────────────────────────────────────────────

var workerCommand = new Command("worker", "Start a DotnetFleet worker that connects to a coordinator");

var coordinatorUrlOption = new Option<string>("--coordinator", "-c")
{
    Description = "Coordinator URL (e.g. http://myserver:5000)",
    Required = true
};
var workerTokenOption = new Option<string?>("--token", "-t")
{
    Description = "Registration token (required on first run)"
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

workerCommand.Options.Add(coordinatorUrlOption);
workerCommand.Options.Add(workerTokenOption);
workerCommand.Options.Add(nameOption);
workerCommand.Options.Add(workerDataDirOption);
workerCommand.Options.Add(pollIntervalOption);
workerCommand.Options.Add(maxDiskOption);

workerCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var coordinatorUrl = parseResult.GetValue(coordinatorUrlOption)!;
    var regToken = parseResult.GetValue(workerTokenOption);
    var workerName = parseResult.GetValue(nameOption) ?? Environment.MachineName;
    var dataDir = parseResult.GetValue(workerDataDirOption)
                  ?? FleetConfig.GetDefaultDataDir($"worker-{workerName}");
    var pollInterval = parseResult.GetValue(pollIntervalOption);
    var maxDisk = parseResult.GetValue(maxDiskOption);

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    Console.WriteLine();
    Console.WriteLine($"  DotnetFleet Worker \"{workerName}\"");
    Console.WriteLine($"  Coordinator: {coordinatorUrl}");
    Console.WriteLine($"  Data dir:    {dataDir}");
    Console.WriteLine();

    try
    {
        var options = new WorkerStartupOptions
        {
            CoordinatorUrl = coordinatorUrl,
            RegistrationToken = regToken,
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
