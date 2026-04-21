using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DotnetFleet.Tool;

/// <summary>
/// Manages auto-generated secrets (JWT secret, registration token) for the coordinator.
/// Persists them to a JSON config file so they survive restarts.
/// </summary>
public static class FleetConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public record CoordinatorConfig(
        string JwtSecret,
        string RegistrationToken,
        int Port = 5000);

    public static string GetDefaultDataDir(string component)
    {
        var home = GetEffectiveUserHome();
        return Path.Combine(home, ".fleet", component);
    }

    /// <summary>
    /// When running under sudo, <see cref="Environment.SpecialFolder.UserProfile"/> returns
    /// root's home. This method resolves the original user's home via SUDO_USER + /etc/passwd.
    /// </summary>
    private static string GetEffectiveUserHome()
    {
        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        if (sudoUser != null && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                foreach (var line in File.ReadLines("/etc/passwd"))
                {
                    var fields = line.Split(':');
                    if (fields.Length >= 6 && fields[0] == sudoUser)
                        return fields[5];
                }
            }
            catch { }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static CoordinatorConfig LoadOrCreateCoordinatorConfig(string dataDir, string? jwtSecretOverride, string? tokenOverride, int port)
    {
        Directory.CreateDirectory(dataDir);
        var configPath = Path.Combine(dataDir, "config.json");

        CoordinatorConfig? existing = null;
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                existing = JsonSerializer.Deserialize<CoordinatorConfig>(json, JsonOptions);
            }
            catch
            {
                // Corrupted file — regenerate
            }
        }

        var jwtSecret = jwtSecretOverride
                        ?? existing?.JwtSecret
                        ?? GenerateSecret(64);

        var registrationToken = tokenOverride
                                ?? existing?.RegistrationToken
                                ?? GenerateSecret(32);

        var config = new CoordinatorConfig(jwtSecret, registrationToken, port);

        // Persist (always, to capture any overrides)
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));

        return config;
    }

    private static string GenerateSecret(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(length, chars, (span, c) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = c[RandomNumberGenerator.GetInt32(c.Length)];
        });
    }
}
