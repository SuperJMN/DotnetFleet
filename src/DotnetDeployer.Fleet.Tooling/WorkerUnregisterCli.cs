using System.CommandLine;

namespace DotnetDeployer.Fleet.Tooling;

internal static class WorkerUnregisterCli
{
    public static Command Create(Option<string?> coordinatorUrlOption, Option<bool> noDiscoverOption)
    {
        var command = new Command("unregister", "Unregister a worker from the coordinator");
        var nameOption = new Option<string?>("--name", "-n")
        {
            Description = "Worker name to unregister"
        };
        var adminUserOption = new Option<string>("--admin-user")
        {
            Description = "Admin username (default: admin)",
            DefaultValueFactory = _ => "admin"
        };
        var adminPasswordOption = new Option<string>("--admin-password")
        {
            Description = "Admin password (default: admin)",
            DefaultValueFactory = _ => "admin"
        };

        command.Options.Add(nameOption);
        command.Options.Add(coordinatorUrlOption);
        command.Options.Add(noDiscoverOption);
        command.Options.Add(adminUserOption);
        command.Options.Add(adminPasswordOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var workerName = parseResult.GetValue(nameOption);
            if (string.IsNullOrWhiteSpace(workerName))
            {
                Console.Error.WriteLine("Error: --name is required.");
                return 1;
            }

            var coordinatorUrl = await ResolveCoordinatorUrl(
                parseResult.GetValue(coordinatorUrlOption),
                parseResult.GetValue(noDiscoverOption));
            if (coordinatorUrl is null)
                return 1;

            using var http = new HttpClient
            {
                BaseAddress = new Uri(coordinatorUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(15)
            };

            try
            {
                var client = new FleetAdminClient(http);
                var result = await client.UnregisterWorkerByName(
                    workerName,
                    parseResult.GetValue(adminUserOption) ?? "admin",
                    parseResult.GetValue(adminPasswordOption) ?? "admin",
                    cancellationToken);

                return PrintResult(result, workerName, coordinatorUrl);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Error: coordinator request failed: {ex.Message}");
                return 1;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Error: coordinator request timed out.");
                return 1;
            }
        });

        return command;
    }

    private static async Task<string?> ResolveCoordinatorUrl(string? explicitUrl, bool noDiscover)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            if (!CoordinatorResolver.TryNormalizeCoordinatorUrl(explicitUrl, out var normalized, out var error))
            {
                Console.Error.WriteLine($"Error: --coordinator value '{explicitUrl}' is invalid: {error}");
                return null;
            }

            return normalized;
        }

        if (noDiscover)
        {
            Console.Error.WriteLine("Error: --coordinator is required when --no-discover is set.");
            return null;
        }

        var local = LocalCoordinatorDiscovery.TryDiscover();
        if (local is not null)
        {
            Console.WriteLine($"  ✓ Discovered local coordinator at {local.Url} (source: {local.Source})");
            return local.Url;
        }

        var found = await MdnsCoordinatorDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3));
        if (found.Count == 1)
        {
            var only = found[0];
            Console.WriteLine($"  ✓ Discovered coordinator via mDNS: {only.Url} (instance: {only.Instance})");
            return only.Url;
        }

        if (found.Count > 1)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Error: found {found.Count} coordinators on the LAN — pick one with --coordinator:");
            foreach (var coordinator in found)
                Console.Error.WriteLine($"   • {coordinator.Url}  (instance: {coordinator.Instance}, version: {coordinator.Version})");
            return null;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Error: no coordinator found.");
        Console.Error.WriteLine("       Pass --coordinator <url>, or run this on the coordinator host.");
        return null;
    }

    private static int PrintResult(WorkerUnregisterResult result, string requestedName, string coordinatorUrl)
    {
        switch (result.Status)
        {
            case WorkerUnregisterStatus.Deleted:
                Console.WriteLine($"  ✓ Worker '{result.WorkerName}' ({result.WorkerId}) unregistered from {coordinatorUrl}.");
                if (result.FailedJobs > 0)
                    Console.WriteLine($"    Failed live jobs: {result.FailedJobs}");
                return 0;

            case WorkerUnregisterStatus.NotFound:
                Console.Error.WriteLine($"Error: worker '{requestedName}' was not found on {coordinatorUrl}.");
                return 1;

            case WorkerUnregisterStatus.AuthenticationFailed:
                Console.Error.WriteLine("Error: admin login failed. Check --admin-user and --admin-password.");
                return 1;

            case WorkerUnregisterStatus.Forbidden:
                Console.Error.WriteLine("Error: the authenticated user is not allowed to manage workers.");
                return 1;

            case WorkerUnregisterStatus.Ambiguous:
                Console.Error.WriteLine($"Error: worker name '{requestedName}' matched {result.Matches} workers; use the exact registered name.");
                return 1;

            case WorkerUnregisterStatus.DeleteEndpointUnavailable:
                Console.Error.WriteLine($"Error: worker '{result.WorkerName}' exists, but this coordinator does not expose worker unregister.");
                Console.Error.WriteLine("       Update/restart the coordinator with a build that includes DELETE /api/workers/{id}.");
                return 1;

            default:
                Console.Error.WriteLine("Error: worker unregister failed.");
                return 1;
        }
    }
}
