using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CloudOrc.ControlAgent.Workspace;

/// <summary>
/// Endpoint-local evidence for remote access readiness (handoff Part A §6, §6.1 endpointHealth):
/// TSPlus services / HTML5 listener / local HTTP, RDP port, Windows Firewall profiles and network
/// identity. Observation only — it never restarts TSPlus or changes the firewall (§6 "Agent does NOT
/// own ... repeated blind restarts"); CloudOrc decides with the network probes whether the problem is
/// the route or TSPlus.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EndpointHealthReporter(BackendApiClient api, ILogger<EndpointHealthReporter> logger) : BackgroundService
{
    public const string Route = "api/v1/remote-workspace/agent/endpoint-health";
    private static readonly string[] TsplusServices = ["SVCW", "SVCM", "SVCE"];
    private static readonly int[] WatchedPorts = [80, 443];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (api.IsConfigured)
                {
                    var report = await CollectAsync(stoppingToken).ConfigureAwait(false);
                    var r = await api.PostJsonStringAsync(Route, JsonSerializer.Serialize(report, BackendApiClient.SnakeCaseJson), stoppingToken).ConfigureAwait(false);
                    if (!r.IsSuccess) logger.LogDebug("Endpoint health not accepted: {Outcome}.", r.Describe());
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Endpoint health collection failed.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal static async Task<EndpointReport> CollectAsync(CancellationToken ct)
    {
        var services = new Dictionary<string, string>();
        foreach (var name in TsplusServices)
        {
            try
            {
                using var sc = new ServiceController(name);
                services[name] = sc.Status.ToString();
            }
            catch (InvalidOperationException)
            {
                services[name] = "Missing";
            }
        }

        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        int? httpStatus = null;
        string? httpError = null;
        try
        {
            // Do not follow redirects: TSPlus HTML5 answers 301 -> https with a self-signed certificate;
            // "answers at all" is what local health means (TLS of the public name is checked elsewhere).
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync("http://127.0.0.1/", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            httpStatus = (int)resp.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            httpError = ex is HttpRequestException { InnerException: SocketException se } ? se.SocketErrorCode.ToString() : ex.GetType().Name;
        }

        var rdpPort = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "PortNumber", null) as int?;
        bool? Fw(string profile) =>
            Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}", "EnableFirewall", null) is int v ? v == 1 : null;

        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .FirstOrDefault(p => p.GatewayAddresses.Count > 0);
        var ipv4 = nic?.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

        return new EndpointReport
        {
            AgentVersion = AgentVersionInfo.GetVersion(typeof(EndpointHealthReporter).Assembly),
            Tsplus = new TsplusHealth
            {
                Services = services,
                ListeningPorts = WatchedPorts.Where(listeners.Contains).ToList(),
                LocalHttpStatus = httpStatus,
                LocalHttpError = httpError
            },
            Rdp = new RdpHealth { Port = rdpPort, Listening = rdpPort is { } p && listeners.Contains(p) },
            Firewall = new FirewallHealth { Domain = Fw("DomainProfile"), Private = Fw("StandardProfile"), Public = Fw("PublicProfile") },
            Network = new NetworkHealth
            {
                Ip = ipv4?.Address.ToString(),
                PrefixLength = ipv4?.PrefixLength,
                Gateway = nic?.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString(),
                Dns = nic?.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList()
            }
        };
    }

    internal sealed class EndpointReport
    {
        [JsonPropertyName("agent_version")] public string? AgentVersion { get; init; }
        [JsonPropertyName("tsplus")] public required TsplusHealth Tsplus { get; init; }
        [JsonPropertyName("rdp")] public required RdpHealth Rdp { get; init; }
        [JsonPropertyName("firewall")] public required FirewallHealth Firewall { get; init; }
        [JsonPropertyName("network")] public required NetworkHealth Network { get; init; }
    }

    internal sealed class TsplusHealth
    {
        [JsonPropertyName("services")] public required Dictionary<string, string> Services { get; init; }
        [JsonPropertyName("listeningPorts")] public required List<int> ListeningPorts { get; init; }
        [JsonPropertyName("localHttpStatus")] public int? LocalHttpStatus { get; init; }
        [JsonPropertyName("localHttpError")] public string? LocalHttpError { get; init; }
    }

    internal sealed class RdpHealth
    {
        [JsonPropertyName("port")] public int? Port { get; init; }
        [JsonPropertyName("listening")] public bool Listening { get; init; }
    }

    internal sealed class FirewallHealth
    {
        [JsonPropertyName("domain")] public bool? Domain { get; init; }
        [JsonPropertyName("private")] public bool? Private { get; init; }
        [JsonPropertyName("public")] public bool? Public { get; init; }
    }

    internal sealed class NetworkHealth
    {
        [JsonPropertyName("ip")] public string? Ip { get; init; }
        [JsonPropertyName("prefixLength")] public int? PrefixLength { get; init; }
        [JsonPropertyName("gateway")] public string? Gateway { get; init; }
        [JsonPropertyName("dns")] public List<string>? Dns { get; init; }
    }
}
