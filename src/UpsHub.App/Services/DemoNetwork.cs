using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UpsHub.Core.Discovery;
using UpsHub.FakeNut;

namespace UpsHub.App.Services;

/// <summary>
/// Demo mode network: a few fake upsd instances on 127.0.0.x and a simulated LAN adapter, so discovery
/// can be shown without touching the real network. Probes get artificial latency so progress is visible.
/// </summary>
internal sealed class DemoNetwork : IAsyncDisposable
{
    public const string NasUser = "monuser", NasPassword = "secret";
    private readonly List<FakeNutServer> _servers = new();
    private readonly HashSet<int> _ports = new();
    private bool _started;

    /// <summary>When true, scans an empty simulated network (troubleshooting screen).</summary>
    public bool Empty { get; set; }

    private void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        Start("127.0.0.20", s => s.Add(FakeUps.ApcBackUps("apc")));
        Start("127.0.0.31", s =>
        {
            s.Version = "Network UPS Tools upsd 2.7.4 - http://www.networkupstools.org/";
            s.RequiredUsername = NasUser; s.RequiredPassword = NasPassword;
            s.Add(FakeUpsPresets.EatonEllipse("ups"));
        });
        Start("127.0.0.45", s =>
        {
            s.Version = "Network UPS Tools upsd 2.8.2 - https://www.networkupstools.org/";
            s.StartTlsSupported = true;
            s.Add(FakeUpsPresets.EatonEllipse("rack-a")).Add(FakeUpsPresets.CyberPower("closet"));
        });
    }

    private void Start(string ip, Action<FakeNutServer> setup)
    {
        FakeNutServer s;
        try { s = new FakeNutServer(3493, IPAddress.Parse(ip)); setup(s); s.Start(); }
        catch (SocketException) { s = new FakeNutServer(0, IPAddress.Parse(ip)); setup(s); s.Start(); } // 3493 taken
        _servers.Add(s);
        _ports.Add(s.Port);
    }

    public NutDiscovery CreateDiscovery()
    {
        EnsureStarted();
        var inv = new StaticNetworkInventory();
        inv.Adapters.Add(new AdapterInfo("Ethernet", "Intel(R) Ethernet Connection I219-V", NetworkInterfaceType.Ethernet,
            IPAddress.Parse(Empty ? "127.0.9.1" : "127.0.0.1"), 24));
        inv.Adapters.Add(new AdapterInfo("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet,
            IPAddress.Parse("172.25.160.1"), 20));
        if (!Empty) inv.Neighbors.Add(new NeighborEntry(IPAddress.Parse("127.0.0.31"), "00:11:32:aa:bb:cc"));
        var rack = _servers[2];
        return new NutDiscovery(inv, Empty ? null : new DemoMdns(new MdnsService("Rack UPS", "nut-rack.local", rack.Port, new[] { IPAddress.Parse("127.0.0.45") })));
    }

    public void Tweak(DiscoveryOptions o)
    {
        EnsureStarted();
        o.IncludeLoopbackAdapters = true;
        o.ResolveHostNames = false;
        o.Concurrency = 16;
        o.SimulatedLatency = TimeSpan.FromMilliseconds(180);
        o.MdnsDuration = TimeSpan.FromSeconds(1.5);
        foreach (var p in _ports) if (!o.Ports.Contains(p)) o.Ports.Add(p);
        if (Empty) { o.IncludeLocalhost = false; o.SavedHosts.Clear(); o.UseMdns = false; }
    }

    private sealed class DemoMdns(MdnsService svc) : IMdnsBrowser
    {
        public async Task BrowseAsync(string serviceType, IEnumerable<IPAddress> interfaceAddresses, TimeSpan duration,
            Action<MdnsService> onFound, CancellationToken ct)
        {
            try { await Task.Delay(900, ct); onFound(svc); await Task.Delay(duration - TimeSpan.FromMilliseconds(900), ct); }
            catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
    }
}
