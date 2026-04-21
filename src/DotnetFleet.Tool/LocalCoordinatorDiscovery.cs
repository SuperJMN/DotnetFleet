using System.Text.Json;

namespace DotnetFleet.Tool;

/// <summary>
/// Locates a DotnetFleet coordinator running on the same machine and returns its URL +
/// registration token, so a worker can self-bootstrap without the user having to copy
/// secrets between commands.
///
/// Discovery order:
///   1. systemd unit at /etc/systemd/system/fleet-coordinator.service
///        → parse WorkingDirectory= → load &lt;dir&gt;/config.json
///   2. ~/.fleet/coordinator/config.json for the effective user (SUDO_USER or current)
/// </summary>
public static class LocalCoordinatorDiscovery
{
    private const string SystemdUnitPath = "/etc/systemd/system/fleet-coordinator.service";

    public sealed record Result(string Url, string Token, string Source);

    public static Result? TryDiscover()
    {
        return TryFromSystemdUnit() ?? TryFromUserHome();
    }

    public static Result? TryFromSystemdUnit()
    {
        if (!File.Exists(SystemdUnitPath))
            return null;

        var workingDir = ReadUnitProperty(SystemdUnitPath, "WorkingDirectory");
        if (string.IsNullOrWhiteSpace(workingDir))
            return null;

        return TryLoadFromDataDir(workingDir, source: $"systemd unit ({SystemdUnitPath})");
    }

    public static Result? TryFromUserHome()
    {
        var homes = new List<string>();
        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        if (!string.IsNullOrEmpty(sudoUser))
        {
            var home = ResolveUserHome(sudoUser);
            if (home != null) homes.Add(home);
        }

        var currentHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(currentHome) && !homes.Contains(currentHome))
            homes.Add(currentHome);

        foreach (var home in homes)
        {
            var dataDir = Path.Combine(home, ".fleet", "coordinator");
            var result = TryLoadFromDataDir(dataDir, source: $"{Path.Combine(dataDir, "config.json")}");
            if (result != null) return result;
        }

        return null;
    }

    private static Result? TryLoadFromDataDir(string dataDir, string source)
    {
        var configPath = Path.Combine(dataDir, "config.json");
        if (!File.Exists(configPath))
            return null;

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? token = null;
            int port = 5000;

            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, "registrationToken", StringComparison.OrdinalIgnoreCase))
                    token = prop.Value.GetString();
                else if (string.Equals(prop.Name, "port", StringComparison.OrdinalIgnoreCase)
                         && prop.Value.ValueKind == JsonValueKind.Number)
                    port = prop.Value.GetInt32();
            }

            if (string.IsNullOrEmpty(token))
                return null;

            return new Result($"http://localhost:{port}", token, source);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadUnitProperty(string unitPath, string propertyName)
    {
        try
        {
            var prefix = propertyName + "=";
            foreach (var raw in File.ReadLines(unitPath))
            {
                var line = raw.TrimStart();
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    return line[prefix.Length..].Trim();
            }
        }
        catch
        {
            // Best-effort
        }
        return null;
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
}
