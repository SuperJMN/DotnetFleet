using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DotnetFleet.Coordinator.Services;

/// <summary>
/// Publishes a multicast DNS (mDNS) advertisement so that workers on the same LAN
/// can auto-discover this coordinator without needing the URL configured manually.
///
/// Service type: <c>_dotnetfleet._tcp.local.</c>
///
/// The TXT record carries non-secret metadata (host name, version). The registration
/// token is **never** advertised — workers still authenticate via the standard flow.
/// </summary>
public sealed class MdnsAdvertiser : IHostedService, IDisposable
{
    public const string ServiceType = "_dotnetfleet._tcp";

    private readonly int port;
    private readonly string instanceName;
    private readonly string version;
    private MulticastService? mdns;
    private ServiceDiscovery? discovery;
    private ServiceProfile? profile;

    public MdnsAdvertiser(int port, string instanceName, string version)
    {
        this.port = port;
        this.instanceName = instanceName;
        this.version = version;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            mdns = new MulticastService();
            discovery = new ServiceDiscovery(mdns);

            var addresses = MulticastService.GetIPAddresses()
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(a))
                .ToList();

            profile = new ServiceProfile(instanceName, ServiceType, (ushort)port, addresses);

            // Replace the default txtvers TXT record with our own metadata.
            profile.Resources.RemoveAll(r => r is TXTRecord);
            profile.Resources.Add(new TXTRecord
            {
                Name = profile.FullyQualifiedName,
                Strings =
                {
                    "txtvers=1",
                    $"instance={instanceName}",
                    $"version={version}",
                    "service=DotnetFleet"
                },
                TTL = TimeSpan.FromMinutes(75)
            });

            discovery.Advertise(profile);
            mdns.Start();
            discovery.Announce(profile);

            Log.Information("mDNS advertising as {Instance} on port {Port} ({ServiceType}.local)",
                instanceName, port, ServiceType);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to start mDNS advertiser; coordinator will not be discoverable on the LAN");
            Dispose();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (discovery != null && profile != null)
                discovery.Unadvertise(profile);
        }
        catch
        {
            // Best-effort
        }
        finally
        {
            Dispose();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        discovery?.Dispose();
        mdns?.Stop();
        mdns?.Dispose();
        discovery = null;
        mdns = null;
        profile = null;
    }
}
