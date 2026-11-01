using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UpsHub.Core.Discovery;
using UpsHub.FakeNut;

namespace UpsHub.Tests;

public class SubnetMathTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Theory]
    [InlineData("192.0.2.77", 24, "192.0.2.0/24", 254UL, "192.0.2.1", "192.0.2.254")]
    [InlineData("10.0.5.9", 22, "10.0.4.0/22", 1022UL, "10.0.4.1", "10.0.7.254")]
    [InlineData("172.16.9.3", 30, "172.16.9.0/30", 2UL, "172.16.9.1", "172.16.9.2")]
    [InlineData("100.64.0.0", 31, "100.64.0.0/31", 2UL, "100.64.0.0", "100.64.0.1")]
    [InlineData("8.8.8.8", 32, "8.8.8.8/32", 1UL, "8.8.8.8", "8.8.8.8")]
    public void Subnet_bounds(string addr, int prefix, string cidr, ulong hosts, string first, string last)
    {
        var sn = Ipv4Subnet.Of(Ip(addr), prefix);
        Assert.Equal(cidr, sn.ToString());
        Assert.Equal(hosts, sn.HostCount);
        Assert.Equal(first, Ipv4.FromUInt(sn.FirstHost).ToString());
        Assert.Equal(last, Ipv4.FromUInt(sn.LastHost).ToString());
        Assert.Equal((int)hosts, sn.Hosts().Count());
        Assert.True(sn.Contains(Ip(addr)));
    }

    [Fact]
    public void Top_of_address_space_does_not_overflow()
    {
        var sn = Ipv4Subnet.Of(Ip("255.255.255.254"), 31);
        Assert.Equal(new[] { "255.255.255.254", "255.255.255.255" }, sn.Hosts().Select(h => h.ToString()));
    }

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.252.0", 22)]
    [InlineData("255.0.0.0", 8)]
    [InlineData("255.255.0.255", -1)]
    public void Mask_to_prefix(string mask, int prefix) => Assert.Equal(prefix, Ipv4.PrefixFromMask(Ip(mask)));

    [Theory]
    [InlineData("10.1.2.3", true)] [InlineData("172.20.0.1", true)] [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)] [InlineData("100.100.1.1", false)] [InlineData("8.8.8.8", false)]
    public void Private_ranges(string ip, bool expected) => Assert.Equal(expected, Ipv4.IsPrivate(Ipv4.ToUInt(Ip(ip))));

    [Theory]
    [InlineData("1.2.3")] [InlineData("1.2.3.256")] [InlineData("10")] [InlineData("a.b.c.d")] [InlineData("1.2.3.4.5")]
    public void Strict_ipv4_parse_rejects(string s) => Assert.False(Ipv4.TryParse(s, out _));

    private static AdapterInfo Lan(string ip, int prefix, string name = "Ethernet") =>
        new(name, "Intel(R) Ethernet Connection", NetworkInterfaceType.Ethernet, Ip(ip), prefix);

    [Fact]
    public void Planner_full_sweep_up_to_slash22()
    {
        Assert.Equal("192.0.2.0/24", ScanPlanner.Plan(Lan("192.0.2.50", 24))!.Subnet.ToString());
        Assert.Equal("10.0.4.0/22", ScanPlanner.Plan(Lan("10.0.5.9", 22))!.Subnet.ToString());
        Assert.Equal("whole subnet", ScanPlanner.Plan(Lan("10.0.5.9", 23))!.Reason);
    }

    [Fact]
    public void Planner_big_networks_get_local_slash24()
    {
        var r = ScanPlanner.Plan(Lan("10.20.30.40", 16))!;
        Assert.Equal("10.20.30.0/24", r.Subnet.ToString());
        Assert.Contains("/16", r.Reason);
        Assert.Equal("10.20.0.0/24", ScanPlanner.Plan(Lan("10.20.0.5", 8))!.Subnet.ToString());
        Assert.Null(ScanPlanner.Plan(Lan("10.9.9.9", 32)));
        Assert.Equal("172.16.0.0/20", ScanPlanner.Plan(Lan("172.16.3.3", 20), maxFullPrefix: 20)!.Subnet.ToString());
    }

    [Fact]
    public void Planner_dedupes_overlaps()
    {
        var ranges = ScanPlanner.Dedupe(new[]
        {
            ScanPlanner.Plan(Lan("192.168.2.10", 24, "Wi-Fi"))!,
            ScanPlanner.Plan(Lan("192.168.0.20", 22, "Ethernet"))!,
            ScanPlanner.Plan(Lan("192.168.2.11", 24, "Ethernet 2"))!,
            ScanPlanner.Plan(Lan("10.0.0.2", 24))!,
        });
        Assert.Equal(new[] { "192.168.0.0/22", "10.0.0.0/24" }, ranges.Select(r => r.Subnet.ToString()));
    }

    [Fact]
    public void Target_spec_parsing()
    {
        var t = TargetSpec.Parse("192.168.5.0/30, 10.0.0.5-7 10.0.0.6; 10.0.1.250-10.0.2.1\nnas.local bad/99 9.9.9.9");
        Assert.Equal(new[] { "192.168.5.1", "192.168.5.2", "10.0.0.5", "10.0.0.6", "10.0.0.7",
                             "10.0.1.250", "10.0.1.251", "10.0.1.252", "10.0.1.253", "10.0.1.254", "10.0.1.255", "10.0.2.0", "10.0.2.1", "9.9.9.9" },
            t.Addresses.Select(a => Ipv4.FromUInt(a).ToString()));
        Assert.Equal(new[] { "nas.local" }, t.HostNames);
        Assert.Single(t.Errors);
        Assert.NotEmpty(TargetSpec.Parse("10.0.0.0/8").Errors); // too large
        Assert.NotEmpty(TargetSpec.Parse("10.0.0.9-3").Errors);  // reversed
        Assert.Equal(new[] { 3493, 3494, 13493 }, TargetSpec.ParsePorts("3494, 13493 3493 x 70000"));
    }

    [Fact]
    public void Host_port_split()
    {
        Assert.Equal(("nas", (int?)3494), NutDiscovery.SplitHostPort("nas:3494"));
        Assert.Equal(("10.0.0.2", (int?)null), NutDiscovery.SplitHostPort("10.0.0.2"));
        Assert.Equal(("fe80::1", (int?)3493), NutDiscovery.SplitHostPort("[fe80::1]:3493"));
        Assert.Equal(("fe80::1", (int?)null), NutDiscovery.SplitHostPort("fe80::1"));
    }
}

public class AdapterFilterTests
{
    [Theory]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection I219-V", NetworkInterfaceType.Ethernet, false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211, false)]
    [InlineData("Local Area Connection", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet, false)]
    [InlineData("vEthernet (External LAN)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, false)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, true)]
    [InlineData("vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter #2", NetworkInterfaceType.Ethernet, true)]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter", NetworkInterfaceType.Ethernet, true)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", NetworkInterfaceType.Ethernet, true)]
    [InlineData("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Unknown, true)]
    [InlineData("OpenVPN Wintun", "Wintun Userspace Tunnel", NetworkInterfaceType.Unknown, true)]
    [InlineData("Ethernet 3", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, true)]
    [InlineData("Ethernet 4", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", NetworkInterfaceType.Ethernet, true)]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", NetworkInterfaceType.Ethernet, true)]
    [InlineData("eth0", "eth0", NetworkInterfaceType.Ethernet, false)]
    [InlineData("enp3s0", "enp3s0", NetworkInterfaceType.Ethernet, false)]
    [InlineData("vmbr0", "vmbr0", NetworkInterfaceType.Ethernet, false)]
    [InlineData("docker0", "docker0", NetworkInterfaceType.Ethernet, true)]
    [InlineData("br-1a2b3c", "br-1a2b3c", NetworkInterfaceType.Ethernet, true)]
    [InlineData("veth12ab", "veth12ab", NetworkInterfaceType.Ethernet, true)]
    [InlineData("virbr0", "virbr0", NetworkInterfaceType.Ethernet, true)]
    [InlineData("wg0", "wg0", NetworkInterfaceType.Unknown, true)]
    [InlineData("tun0", "tun0", NetworkInterfaceType.Unknown, true)]
    [InlineData("lo", "lo", NetworkInterfaceType.Loopback, true)]
    public void Virtual_adapter_detection(string name, string description, NetworkInterfaceType type, bool isVirtual) =>
        Assert.Equal(isVirtual, AdapterFilter.IsVirtual(name, description, type));
}

public class NeighborParserTests
{
    [Fact]
    public void Linux_proc_net_arp()
    {
        const string text = """
            IP address       HW type     Flags       HW address            Mask     Device
            192.0.2.1      0x1         0x2         aa:bb:cc:dd:ee:01     *        eth0
            192.0.2.20     0x1         0x2         aa:bb:cc:dd:ee:20     *        vmbr0
            192.0.2.99     0x1         0x0         00:00:00:00:00:00     *        eth0
            224.0.0.251      0x1         0x6         01:00:5e:00:00:fb     *        eth0
            """;
        var n = NeighborParsers.ParseProcNetArp(text);
        Assert.Equal(new[] { "192.0.2.1", "192.0.2.20" }, n.Select(x => x.Address.ToString()));
        Assert.Equal("vmbr0", n[1].Interface);
    }

    [Fact]
    public void Windows_arp_a_any_language()
    {
        const string text = """

            Schnittstelle: 192.0.2.10 --- 0xb
              Internetadresse       Physische Adresse     Typ
              192.0.2.1           aa-bb-cc-dd-ee-01     dynamisch
              192.0.2.20          aa-bb-cc-dd-ee-20     dynamisch
              192.0.2.255         ff-ff-ff-ff-ff-ff     statisch
              224.0.0.22            01-00-5e-00-00-16     statisch
              239.255.255.250       01-00-5e-7f-ff-fa     statisch
              255.255.255.255       ff-ff-ff-ff-ff-ff     statisch
            """;
        var n = NeighborParsers.ParseWindowsArp(text);
        Assert.Equal(new[] { "192.0.2.1", "192.0.2.20" }, n.Select(x => x.Address.ToString()));
        Assert.Equal("aa:bb:cc:dd:ee:20", n[1].Mac);
    }

    [Fact]
    public void Bsd_arp_an()
    {
        const string text = """
            ? (192.0.2.1) at aa:bb:cc:dd:ee:1 on en0 ifscope [ethernet]
            ? (192.0.2.30) at (incomplete) on en0 ifscope [ethernet]
            ? (192.0.2.40) at 0:11:22:33:44:55 on en0 ifscope [ethernet]
            """;
        Assert.Equal(new[] { "192.0.2.1", "192.0.2.40" }, NeighborParsers.ParseBsdArp(text).Select(x => x.Address.ToString()));
    }
}

public class MdnsParserTests
{
    [Fact]
    public void Query_has_qu_bit_and_ptr_type()
    {
        var q = DnsMessage.BuildQuery("_nut._tcp.local", DnsMessage.TypePtr, unicastResponse: true);
        Assert.Equal(new byte[] { 0, 1 }, q[4..6]);                  // QDCOUNT
        Assert.Equal(new byte[] { 0, 12, 0x80, 1 }, q[^4..]);        // PTR, QU|IN
        Assert.Equal(4, q[12]); Assert.Equal((byte)'_', q[13]);      // "_nut"
    }

    [Fact]
    public void Response_with_ptr_srv_a_resolves_service()
    {
        var pkt = DnsMessage.BuildResponse(new[]
        {
            new DnsMessage.Record("_nut._tcp.local", DnsMessage.TypePtr, 4500, Text: "upsd on pve._nut._tcp.local"),
            new DnsMessage.Record("upsd on pve._nut._tcp.local", DnsMessage.TypeSrv, 120, Text: "nut.local", Port: 3493),
            new DnsMessage.Record("upsd on pve._nut._tcp.local", DnsMessage.TypeTxt, 4500, Text: "txtvers=1"),
            new DnsMessage.Record("nut.local", DnsMessage.TypeA, 120, Address: IPAddress.Parse("192.0.2.20")),
        });
        Assert.True(DnsMessage.TryParse(pkt, out var msg));
        Assert.True(msg.IsResponse);
        Assert.Equal(4, msg.Records.Count);
        var acc = new MdnsAccumulator("_nut._tcp.local");
        var found = acc.Add(msg, IPAddress.Parse("192.0.2.20"));
        var svc = Assert.Single(found);
        Assert.Equal("upsd on pve", svc.Instance);
        Assert.Equal("nut.local", svc.Target);
        Assert.Equal(3493, svc.Port);
        Assert.Equal("192.0.2.20", svc.Addresses.Single().ToString());
    }

    [Fact]
    public void Records_split_across_packets_and_compressed_names()
    {
        var acc = new MdnsAccumulator("_nut._tcp.local");
        // Hand-built packet with a compression pointer: PTR rdata points back at the question name's "_nut._tcp.local".
        var pkt = new List<byte> { 0, 0, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0 };
        void Name(params string[] labels) { foreach (var l in labels) { pkt.Add((byte)l.Length); pkt.AddRange(System.Text.Encoding.ASCII.GetBytes(l)); } }
        Name("_nut", "_tcp", "local"); pkt.Add(0); pkt.AddRange(new byte[] { 0, 12, 0, 1 }); // question at offset 12
        pkt.AddRange(new byte[] { 0xC0, 12, 0, 12, 0, 1, 0, 0, 0x11, 0x94 });                  // answer name -> ptr
        var rdata = new List<byte> { 3, (byte)'n', (byte)'a', (byte)'s', 0xC0, 12 };
        pkt.AddRange(new byte[] { 0, (byte)rdata.Count }); pkt.AddRange(rdata);
        Assert.True(DnsMessage.TryParse(pkt.ToArray(), out var m1));
        Assert.Equal("nas._nut._tcp.local", m1.Records.Single().Text);
        Assert.Empty(acc.Add(m1, null));

        var m2pkt = DnsMessage.BuildResponse(new[]
        {
            new DnsMessage.Record("nas._nut._tcp.local", DnsMessage.TypeSrv, 120, Text: "nas.local", Port: 3494),
            new DnsMessage.Record("nas.local", DnsMessage.TypeA, 120, Address: IPAddress.Parse("10.1.1.5")),
        });
        Assert.True(DnsMessage.TryParse(m2pkt, out var m2));
        var svc = Assert.Single(acc.Add(m2, null));
        Assert.Equal(("nas", 3494, "10.1.1.5"), (svc.Instance, svc.Port, svc.Addresses[0].ToString()));
    }

    [Fact]
    public void Garbage_and_pointer_loops_are_rejected()
    {
        Assert.False(DnsMessage.TryParse(new byte[] { 1, 2, 3 }, out _));
        var loop = new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12 };
        Assert.False(DnsMessage.TryParse(loop, out _));
    }
}

public class DedupeTests
{
    private static DiscoveredServer S(string ip, string? serial = "SN1", bool local = false, string? host = null, int port = 3493)
    {
        var s = new DiscoveredServer { Host = ip, Port = port, Version = "upsd 2.8.1", IsLocalMachine = local, HostName = host };
        s.Addresses.Add(IPAddress.Parse(ip));
        s.Ups.Add(new DiscoveredUps("ups", "", "APC", "Back-UPS", "OL", 100, serial));
        return s;
    }

    [Fact]
    public void Same_serials_merge_identical_boxes_without_serial_do_not()
    {
        Assert.True(NutDiscovery.SameServer(S("192.0.2.20"), S("10.0.0.20")));
        Assert.False(NutDiscovery.SameServer(S("192.0.2.20", null), S("192.0.2.21", null)));
        Assert.False(NutDiscovery.SameServer(S("192.0.2.20"), S("192.0.2.20", port: 3494)));
    }

    [Fact]
    public void Same_hostname_or_this_pc_merges()
    {
        Assert.True(NutDiscovery.SameServer(S("192.0.2.20", null, host: "nut.lan"), S("10.0.0.20", null, host: "NUT.lan")));
        Assert.True(NutDiscovery.SameServer(S("127.0.0.1", null, local: true), S("192.0.2.10", null, local: true)));
    }

    [Fact]
    public void Merge_prefers_private_lan_ip_and_keeps_all_addresses()
    {
        var a = S("100.100.5.5");
        NutDiscovery.Merge(a, S("192.168.50.20", host: "nut.lan"));
        Assert.Equal("192.168.50.20", a.Host);
        Assert.Equal(2, a.Addresses.Count);
        Assert.Equal("nut.lan", a.HostName);
        var local = S("192.0.2.10", local: true);
        NutDiscovery.Merge(local, S("127.0.0.1", local: true));
        Assert.Equal("127.0.0.1", local.Host);
    }
}

public class DiscoveryIntegrationTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private sealed class FakeMdns(MdnsService svc) : IMdnsBrowser
    {
        public async Task BrowseAsync(string type, IEnumerable<IPAddress> ifs, TimeSpan d, Action<MdnsService> onFound, CancellationToken ct)
        {
            Assert.Equal(NutDiscovery.MdnsServiceType, type);
            await Task.Delay(50, ct);
            onFound(svc);
        }
    }

    private static StaticNetworkInventory LoopbackLan(int prefix = 29)
    {
        var inv = new StaticNetworkInventory();
        inv.Adapters.Add(new AdapterInfo("Test LAN", "Simulated LAN adapter", NetworkInterfaceType.Ethernet, IPAddress.Parse("127.0.0.1"), prefix));
        inv.Adapters.Add(new AdapterInfo("docker0", "docker0", NetworkInterfaceType.Ethernet, IPAddress.Parse("172.17.0.1"), 16));
        return inv;
    }

    [Fact]
    public async Task Finds_every_fake_server_on_the_loopback_lan()
    {
        int p = FreePort(), p2 = FreePort(), p3 = FreePort();
        await using var a = new FakeNutServer(p, IPAddress.Parse("127.0.0.2")).Add(FakeUps.ApcBackUps("apc")).Start();
        await using var b = new FakeNutServer(p, IPAddress.Parse("127.0.0.3")) { RequiredUsername = "mon", RequiredPassword = "pw" }
            .Add(FakeUpsPresets.EatonEllipse("rack")).Start();
        await using var c = new FakeNutServer(p, IPAddress.Parse("127.0.0.4")) { StartTlsSupported = true }
            .Add(FakeUpsPresets.EatonEllipse("ups1")).Add(FakeUpsPresets.CyberPower("ups2")).Start();
        await using var d = new FakeNutServer(p2, IPAddress.Any).Add(new FakeUps("local", "On this PC").Set("ups.status", "OL").Set("device.serial", "LOCAL-1")).Start();
        await using var e = new FakeNutServer(p3, IPAddress.Parse("127.0.0.6")).Add(FakeUpsPresets.CyberPower("office")).Start();

        // Something that is not upsd listening on the NUT port must be ignored.
        var notNut = new TcpListener(IPAddress.Parse("127.0.0.5"), p);
        notNut.Start();
        using var notNutCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!notNutCts.IsCancellationRequested)
            {
                try
                {
                    using var cl = await notNut.AcceptTcpClientAsync(notNutCts.Token);
                    var w = new StreamWriter(cl.GetStream()) { AutoFlush = true };
                    await w.WriteLineAsync("HTTP/1.0 400 Bad Request");
                    cl.Close();
                }
                catch { break; }
            }
        });

        var inv = LoopbackLan();
        inv.Neighbors.Add(new NeighborEntry(IPAddress.Parse("127.0.0.4"), "aa:bb:cc:dd:ee:04"));
        var mdns = new FakeMdns(new MdnsService("Office UPS", "nut-office.local", p3, new[] { IPAddress.Parse("127.0.0.6") }));
        var disco = new NutDiscovery(inv, mdns);
        var reports = new List<DiscoveryProgress>();
        var progress = new SyncProgress<DiscoveryProgress>(r => { lock (reports) reports.Add(r); });

        var sw = Stopwatch.StartNew();
        var result = await disco.RunAsync(new DiscoveryOptions
        {
            Ports = new() { p, p2 },
            IncludeLoopbackAdapters = true,
            ResolveHostNames = false,
            Concurrency = 32,
        }, progress);
        notNutCts.Cancel(); notNut.Stop();

        Assert.False(result.Cancelled);
        Assert.True(5 == result.Servers.Count, string.Join("; ", result.Servers.Select(s => $"{s.Endpoint} [{string.Join(",", s.Addresses)}] {s.Sources} ups={string.Join(",", s.Ups.Select(u => u.Name))} auth={s.AuthRequired}")) + $" probed={result.Probed}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal("127.0.0.0/29", Assert.Single(result.Networks).Subnet.ToString());
        Assert.Contains(result.SkippedAdapters, x => x.Name == "docker0");
        Assert.Equal(1, result.NeighborCount);
        Assert.Equal(1, result.MdnsCount);

        var sa = result.Servers.Single(s => s.Ups.Any(u => u.Name == "apc"));
        Assert.Equal(("127.0.0.2", p), (sa.Host, sa.Port));
        var apc = sa.Ups.Single();
        Assert.Equal(("American Power Conversion", "Back-UPS XS 1400U", "OL", 100.0), (apc.Manufacturer, apc.Model, apc.Status, apc.BatteryCharge));
        Assert.False(sa.AuthRequired);
        Assert.False(sa.TlsSupported);
        Assert.Contains("upsd 2.8.1", sa.Version);
        Assert.True(sa.Sources.HasFlag(DiscoverySource.Subnet));

        var sb = result.Servers.Single(s => s.Host == "127.0.0.3");
        Assert.True(sb.AuthRequired);
        Assert.Empty(sb.Ups);

        var sc = result.Servers.Single(s => s.Host == "127.0.0.4");
        Assert.True(sc.TlsSupported);
        Assert.Equal(new[] { "ups1", "ups2" }, sc.Ups.Select(u => u.Name));
        Assert.Equal(64.0, sc.Ups[1].BatteryCharge);
        Assert.True(sc.Sources.HasFlag(DiscoverySource.Neighbor));

        // One server bound to every loopback address: merged into one entry, reached as 127.0.0.1.
        var sd = result.Servers.Single(s => s.Port == p2);
        Assert.Equal("127.0.0.1", sd.Host);
        Assert.Equal(6, sd.Addresses.Count);
        Assert.True(sd.IsLocalMachine);
        Assert.True(sd.Sources.HasFlag(DiscoverySource.Localhost));

        var se = result.Servers.Single(s => s.Port == p3);
        Assert.Equal(("Office UPS", "nut-office.local", "127.0.0.6"), (se.MdnsInstance, se.HostName, se.Host));
        Assert.Equal(DiscoverySource.Mdns, se.Sources);

        Assert.DoesNotContain(result.Servers, s => s.Addresses.Contains(IPAddress.Parse("127.0.0.5")) && s.Port == p);
        Assert.Equal(result.Probed, reports.Last().Total);
        Assert.Equal(DiscoveryPhase.Done, reports.Last().Phase);
        Assert.Equal(5, reports.Last().Servers.Count);
    }

    [Fact]
    public async Task Custom_targets_and_saved_hosts()
    {
        int p = FreePort();
        await using var a = new FakeNutServer(p, IPAddress.Parse("127.0.0.12")).Add(FakeUps.ApcBackUps("apc")).Start();
        await using var b = new FakeNutServer(p, IPAddress.Parse("127.0.0.40")).Add(FakeUpsPresets.EatonEllipse("eaton")).Start();
        var result = await new NutDiscovery(new StaticNetworkInventory()).RunAsync(new DiscoveryOptions
        {
            Ports = new() { p }, IncludeLocalhost = false, UseMdns = false, ResolveHostNames = false,
            CustomTargets = "127.0.0.10-14",
            SavedHosts = new() { $"localhost:{p}", $"127.0.0.40:{p}" },
        });
        Assert.Equal(new[] { "127.0.0.12", "127.0.0.40" }, result.Servers.Select(s => s.Host).Order());
        Assert.True(result.Servers.Single(s => s.Host == "127.0.0.12").Sources.HasFlag(DiscoverySource.Custom));
        Assert.True(result.Servers.Single(s => s.Host == "127.0.0.40").Sources.HasFlag(DiscoverySource.Saved));
        Assert.Contains("No active network adapter", string.Join(" ", result.Notes));
    }

    [Fact]
    public async Task Cancel_stops_quickly_and_reports_partial_result()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var sw = Stopwatch.StartNew();
        var result = await new NutDiscovery(LoopbackLan(22)).RunAsync(new DiscoveryOptions
        {
            Ports = new() { FreePort() }, IncludeLoopbackAdapters = true, UseMdns = false, ResolveHostNames = false,
            Concurrency = 4, SimulatedLatency = TimeSpan.FromMilliseconds(50),
        }, null, cts.Token);
        Assert.True(result.Cancelled, $"probed={result.Probed} elapsed={sw.Elapsed} servers={result.Servers.Count}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.InRange(result.Probed, 1, 200);
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }
}

public class TlsFallbackTests
{
    [Fact]
    public async Task Prefer_falls_back_to_plain_when_the_handshake_fails()
    {
        // The fake answers "OK STARTTLS" and then hangs up instead of doing a TLS handshake.
        await using var srv = new FakeNutServer() { StartTlsSupported = true }.Add(FakeUps.ApcBackUps("apc")).Start();
        await using (var prefer = new UpsHub.Core.Nut.NutClient(new() { Host = "127.0.0.1", Port = srv.Port, Tls = UpsHub.Core.Nut.TlsMode.Prefer, ConnectTimeout = TimeSpan.FromSeconds(2) }))
        {
            await prefer.ConnectAsync();
            Assert.False(prefer.IsTls);
            Assert.Equal("apc", (await prefer.ListUpsAsync()).Single().Name);
        }
        await using var require = new UpsHub.Core.Nut.NutClient(new() { Host = "127.0.0.1", Port = srv.Port, Tls = UpsHub.Core.Nut.TlsMode.Require, ConnectTimeout = TimeSpan.FromSeconds(2) });
        await Assert.ThrowsAnyAsync<Exception>(() => require.ConnectAsync());
    }
}
