namespace DotnetFleet.Tool;

/// <summary>
/// Resolves the (coordinator URL, registration token) pair that a worker should use,
/// combining explicit CLI flags with the local + mDNS auto-discovery layers.
///
/// Precedence: explicit flags &gt; local on-disk discovery &gt; mDNS LAN discovery.
/// Returns null and prints diagnostics to stderr when nothing usable is found.
/// </summary>
public static class CoordinatorResolver
{
    public sealed record Resolved(string Url, string? Token);

    public static async Task<Resolved?> ResolveAsync(string? explicitUrl, string? explicitToken, bool noDiscover)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            if (!TryNormalizeCoordinatorUrl(explicitUrl!, out var normalized, out var error))
            {
                Console.Error.WriteLine($"Error: --coordinator value '{explicitUrl}' is invalid: {error}");
                return null;
            }
            explicitUrl = normalized;
        }

        // Both flags explicit → trust the user.
        if (!string.IsNullOrWhiteSpace(explicitUrl) && !string.IsNullOrWhiteSpace(explicitToken))
            return new Resolved(explicitUrl!, explicitToken);

        if (noDiscover)
        {
            if (string.IsNullOrWhiteSpace(explicitUrl))
            {
                Console.Error.WriteLine("Error: --coordinator is required when --no-discover is set.");
                return null;
            }
            return new Resolved(explicitUrl!, explicitToken);
        }

        // ── Layer 1: local on-disk discovery (URL + token).
        if (string.IsNullOrWhiteSpace(explicitUrl) || string.IsNullOrWhiteSpace(explicitToken))
        {
            var local = LocalCoordinatorDiscovery.TryDiscover();
            if (local != null)
            {
                var url = !string.IsNullOrWhiteSpace(explicitUrl) ? explicitUrl! : local.Url;
                var token = !string.IsNullOrWhiteSpace(explicitToken) ? explicitToken : local.Token;
                Console.WriteLine($"  ✓ Discovered local coordinator at {local.Url} (source: {local.Source})");
                return new Resolved(url, token);
            }
        }

        // ── Layer 2: mDNS LAN discovery (URL only — token must be provided).
        if (string.IsNullOrWhiteSpace(explicitUrl))
        {
            var found = await MdnsCoordinatorDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3));
            if (found.Count == 1)
            {
                var only = found[0];
                Console.WriteLine($"  ✓ Discovered coordinator via mDNS: {only.Url} (instance: {only.Instance})");
                if (string.IsNullOrWhiteSpace(explicitToken))
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Error: --token is required for LAN-discovered coordinators.");
                    Console.Error.WriteLine($"       Get the token from the coordinator host with: fleet coordinator status");
                    return null;
                }
                return new Resolved(only.Url, explicitToken);
            }

            if (found.Count > 1)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Error: found {found.Count} coordinators on the LAN — pick one with --coordinator:");
                foreach (var c in found)
                    Console.Error.WriteLine($"   • {c.Url}  (instance: {c.Instance}, version: {c.Version})");
                return null;
            }

            // 0 results
            Console.Error.WriteLine();
            Console.Error.WriteLine("Error: no coordinator found.");
            Console.Error.WriteLine("       Pass --coordinator <url> --token <token>, or run 'fleet coordinator install' on the same machine.");
            return null;
        }

        // URL given, token missing.
        if (string.IsNullOrWhiteSpace(explicitToken))
        {
            Console.Error.WriteLine("Error: --token is required when --coordinator points to a remote host and no local config is found.");
            return null;
        }

        return new Resolved(explicitUrl!, explicitToken);
    }

    /// <summary>
    /// Normalizes a coordinator URL provided on the CLI:
    /// - Adds <c>http://</c> when no scheme is present (e.g. <c>192.168.1.10:5000</c>).
    /// - Rejects schemes other than http/https.
    /// - Rejects values that are not parseable absolute URIs.
    /// </summary>
    public static bool TryNormalizeCoordinatorUrl(string input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            error = "value is empty.";
            return false;
        }

        // If there's no scheme (no "://"), assume http for convenience.
        var hasScheme = System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[a-zA-Z][a-zA-Z0-9+\-.]*://");
        var candidate = hasScheme ? trimmed : "http://" + trimmed;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            error = "not a valid absolute URL. Expected something like 'http://host:5000'.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"unsupported scheme '{uri.Scheme}'. Use http or https.";
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
