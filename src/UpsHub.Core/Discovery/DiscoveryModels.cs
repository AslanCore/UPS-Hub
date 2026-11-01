using System.Net;
using UpsHub.Core.Model;

namespace UpsHub.Core.Discovery;

[Flags]
public enum DiscoverySource
{
    None = 0,
    Localhost = 1,
    Saved = 2,
    Mdns = 4,
    Neighbor = 8,
    Subnet = 16,
    Custom = 32,
}

public enum DiscoveryPhase { Preparing, Scanning, Finishing, Done, Cancelled }

public sealed record DiscoveredUps(
    string Name,
    string Description,
    string? Manufacturer,
    string? Model,
    string? Status,
    double? BatteryCharge,
    string? Serial)
{
    public UpsStatus Flags => UpsStatusParser.Parse(Status);
    public UpsHealth Health => UpsStatusParser.HealthOf(Flags);
    public string DisplayModel => string.Join(" ", new[] { Manufacturer, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed class DiscoveredServer
{
    /// <summary>Address or name to connect to (best of <see cref="Addresses"/>).</summary>
    public required string Host { get; set; }
    public required int Port { get; init; }
    public List<IPAddress> Addresses { get; } = new();
    /// <summary>Name from mDNS or reverse DNS, for display.</summary>
    public string? HostName { get; set; }
    public string? MdnsInstance { get; set; }
    public string? Version { get; set; }
    /// <summary>upsd refused LIST/GET without credentials (ACCESS-DENIED).</summary>
    public bool AuthRequired { get; set; }
    /// <summary>null = not checked / unknown.</summary>
    public bool? TlsSupported { get; set; }
    public List<DiscoveredUps> Ups { get; } = new();
    public DiscoverySource Sources { get; set; }
    public TimeSpan Latency { get; set; }
    /// <summary>This PC (loopback or one of our own adapter addresses).</summary>
    public bool IsLocalMachine { get; set; }

    public string Endpoint => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
    public string Title => MdnsInstance ?? HostName ?? Host;

    public string ShortVersion => Version is null ? "upsd"
        : Version.Split(" - ")[0].Replace("Network UPS Tools ", "NUT ").Trim();

    /// <summary>Static identity used to merge the same upsd seen on several addresses.</summary>
    internal string Fingerprint => string.Join("|",
        new[] { Port.ToString(), Version ?? "", AuthRequired ? "auth" : "" }
            .Concat(Ups.OrderBy(u => u.Name, StringComparer.Ordinal).Select(u => $"{u.Name}/{u.Description}/{u.Model}")));

    internal string SerialSet => string.Join(",", Ups.Select(u => u.Serial).Where(s => !string.IsNullOrWhiteSpace(s)).Order());

    public DiscoveredServer Clone()
    {
        var c = new DiscoveredServer
        {
            Host = Host, Port = Port, HostName = HostName, MdnsInstance = MdnsInstance, Version = Version,
            AuthRequired = AuthRequired, TlsSupported = TlsSupported, Sources = Sources, Latency = Latency,
            IsLocalMachine = IsLocalMachine,
        };
        c.Addresses.AddRange(Addresses);
        c.Ups.AddRange(Ups);
        return c;
    }
}

public sealed record DiscoveryProgress(
    DiscoveryPhase Phase,
    int Probed,
    int Total,
    string? Current,
    IReadOnlyList<DiscoveredServer> Servers)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Probed / Total, 0, 1);
}

public sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredServer> Servers,
    IReadOnlyList<ScanRange> Networks,
    IReadOnlyList<AdapterInfo> SkippedAdapters,
    int NeighborCount,
    int MdnsCount,
    int Probed,
    TimeSpan Elapsed,
    bool Cancelled,
    IReadOnlyList<string> Notes);

public sealed class DiscoveryOptions
{
    public List<int> Ports { get; set; } = new() { 3493 };
    public bool IncludeLocalhost { get; set; } = true;
    public bool UseMdns { get; set; } = true;
    public TimeSpan MdnsDuration { get; set; } = TimeSpan.FromSeconds(3);
    public bool UseNeighbors { get; set; } = true;
    public bool ScanSubnets { get; set; } = true;
    public bool IncludeVirtualAdapters { get; set; }
    /// <summary>Tests and demo only: treat 127.x adapters like a LAN so fake servers on 127.0.0.N are swept.</summary>
    public bool IncludeLoopbackAdapters { get; set; }
    /// <summary>Networks up to this prefix are swept fully (22 = 1,022 hosts); bigger ones get the local /24 + ARP hits.</summary>
    public int MaxFullScanPrefix { get; set; } = 22;
    public int Concurrency { get; set; } = 192;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromMilliseconds(400);
    /// <summary>Timeout for likely targets (localhost, saved, mDNS, custom host names).</summary>
    public TimeSpan PriorityConnectTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public bool CheckTls { get; set; } = true;
    public bool ResolveHostNames { get; set; } = true;
    /// <summary>Hosts saved from earlier runs or already configured ("host" or "host:port").</summary>
    public List<string> SavedHosts { get; set; } = new();
    /// <summary>Extra targets typed by the user (see <see cref="TargetSpec"/>).</summary>
    public string? CustomTargets { get; set; }
    /// <summary>Demo only: adds artificial latency per probe so progress is visible on loopback.</summary>
    public TimeSpan SimulatedLatency { get; set; }
}
