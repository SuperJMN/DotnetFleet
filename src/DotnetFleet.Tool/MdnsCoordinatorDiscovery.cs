using System.Collections.Concurrent;
using Makaretu.Dns;

namespace DotnetFleet.Tool;

/// <summary>
/// Browses the LAN via multicast DNS for advertised DotnetFleet coordinators
/// (service type <c>_dotnetfleet._tcp.local.</c>).
///
/// The TXT record carries the instance name and version; the SRV record provides
/// the port, and accompanying A/AAAA records provide the host. The registration
/// token is intentionally NOT advertised — it must be supplied via <c>--token</c>.
/// </summary>
public static class MdnsCoordinatorDiscovery
{
    public const string ServiceType = "_dotnetfleet._tcp";

    public sealed record Found(string Url, string Instance, string Version, string Host);

    public static async Task<List<Found>> DiscoverAsync(TimeSpan timeout)
    {
        var results = new ConcurrentDictionary<string, Found>();

        try
        {
            using var mdns = new MulticastService();
            using var sd = new ServiceDiscovery(mdns);

            sd.ServiceInstanceDiscovered += (_, args) =>
            {
                try
                {
                    var msg = args.Message;
                    var srv = msg.AdditionalRecords.OfType<SRVRecord>()
                              .FirstOrDefault(r => r.Name.Equals(args.ServiceInstanceName))
                              ?? msg.Answers.OfType<SRVRecord>()
                                 .FirstOrDefault(r => r.Name.Equals(args.ServiceInstanceName));
                    if (srv == null) return;

                    var txt = msg.AdditionalRecords.OfType<TXTRecord>()
                              .FirstOrDefault(r => r.Name.Equals(args.ServiceInstanceName))
                              ?? msg.Answers.OfType<TXTRecord>()
                                 .FirstOrDefault(r => r.Name.Equals(args.ServiceInstanceName));

                    var addr = msg.AdditionalRecords.OfType<AddressRecord>()
                               .FirstOrDefault(r => r.Name.Equals(srv.Target))
                               ?? msg.Answers.OfType<AddressRecord>()
                                  .FirstOrDefault(r => r.Name.Equals(srv.Target));

                    var host = addr?.Address?.ToString() ?? srv.Target.ToString().TrimEnd('.');
                    var url = $"http://{host}:{srv.Port}";

                    var instance = ReadTxt(txt, "instance") ?? args.ServiceInstanceName.ToString();
                    var version = ReadTxt(txt, "version") ?? "";

                    results[args.ServiceInstanceName.ToString()] =
                        new Found(url, instance, version, host);
                }
                catch
                {
                    // Ignore malformed responses
                }
            };

            mdns.Start();
            sd.QueryServiceInstances(ServiceType);

            await Task.Delay(timeout);
        }
        catch
        {
            // Multicast may be unavailable (no network, blocked) — return empty.
        }

        return results.Values.ToList();
    }

    private static string? ReadTxt(TXTRecord? txt, string key)
    {
        if (txt == null) return null;
        var prefix = key + "=";
        return txt.Strings
            .FirstOrDefault(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?
            [prefix.Length..];
    }
}
