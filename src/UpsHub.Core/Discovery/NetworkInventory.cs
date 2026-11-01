using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace UpsHub.Core.Discovery;

public sealed record AdapterInfo(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    IPAddress Address,
    int Prefix,
    bool IsUp = true,
    int? Index = null,
    IPAddress? Gateway = null)
{
    public bool IsLoopback => Type == NetworkInterfaceType.Loopback || Ipv4.IsLoopback(Ipv4.ToUInt(Address));
    public bool IsVirtual => AdapterFilter.IsVirtual(Name, Description, Type);
    public Ipv4Subnet Subnet => Ipv4Subnet.Of(Address, Prefix);
    public override string ToString() => $"{Name} {Address}/{Prefix}";
}

public sealed record NeighborEntry(IPAddress Address, string? Mac = null, string? Interface = null);

public interface INetworkInventory
{
    IReadOnlyList<AdapterInfo> GetAdapters();
    Task<IReadOnlyList<NeighborEntry>> GetNeighborsAsync(CancellationToken ct);
}

/// <summary>Decides which adapters are skipped by default (VPNs, hypervisor and container switches).</summary>
public static class AdapterFilter
{
    private static readonly string[] DescriptionKeywords =
    {
        "virtual", "hyper-v", "vmware", "virtualbox", "vbox", "docker", "wsl", "vpn", "tap-windows", "tap adapter",
        "wintun", "wireguard", "tailscale", "zerotier", "openvpn", "fortinet", "fortissl", "anyconnect", "globalprotect",
        "pangp", "juniper", "pulse secure", "hamachi", "npcap loopback", "loopback", "bluetooth", "teredo", "isatap",
        "6to4", "pseudo-interface", "nordlynx", "mullvad", "proton", "expressvpn",
    };

    private static readonly string[] NamePrefixes =
    {
        "docker", "br-", "veth", "virbr", "lxcbr", "lxdbr", "cni", "flannel", "cali", "weave", "kube", "vmnet", "vboxnet",
        "tun", "tap", "wg", "zt", "tailscale", "utun", "ppp", "ipsec", "bluetooth", "lo",
    };

    public static bool IsVirtual(string name, string description, NetworkInterfaceType type)
    {
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return true;
        var n = name.Trim().ToLowerInvariant();
        var d = description.Trim().ToLowerInvariant();

        // Hyper-V: "vEthernet (Default Switch)" / "(WSL ...)" are NAT switches, but an *external* switch
        // ("vEthernet (External)", "vEthernet (LAN)") carries the real LAN, so keep those.
        if (n.StartsWith("vethernet"))
            return n.Contains("default switch") || n.Contains("wsl") || n.Contains("(nat)") || n.Contains("docker") || n.Contains("internal");

        if (NamePrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal) && !LooksPhysicalLinuxName(n))) return true;
        if (DescriptionKeywords.Any(k => d.Contains(k, StringComparison.Ordinal))) return true;
        return false;
    }

    // "lo" prefix must not swallow names like "local area connection"
    private static bool LooksPhysicalLinuxName(string n) => n.StartsWith("local area") || n.StartsWith("lan");
}

/// <summary>Parsers for ARP / neighbour tables from the various OS tools.</summary>
public static partial class NeighborParsers
{
    /// <summary>Linux <c>/proc/net/arp</c>. Flags 0x0 means incomplete.</summary>
    public static List<NeighborEntry> ParseProcNetArp(string text)
    {
        var list = new List<NeighborEntry>();
        foreach (var line in text.Split('\n').Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 6 || !Ipv4.TryParse(f[0], out var ip)) continue;
            if (f[2] == "0x0" || f[3] == "00:00:00:00:00:00") continue;
            Add(list, ip, f[3], f[5]);
        }
        return list;
    }

    [GeneratedRegex(@"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F]{2}(?:[-:][0-9a-fA-F]{2}){5})\b", RegexOptions.Multiline)]
    private static partial Regex WindowsArpLine();

    [GeneratedRegex(@"\((\d{1,3}(?:\.\d{1,3}){3})\)\s+at\s+([0-9a-fA-F]{1,2}(?::[0-9a-fA-F]{1,2}){5})(?:\s+on\s+(\S+))?", RegexOptions.Multiline)]
    private static partial Regex BsdArpLine();

    /// <summary>Windows <c>arp -a</c> (works for any display language: only the data rows match).</summary>
    public static List<NeighborEntry> ParseWindowsArp(string text)
    {
        var list = new List<NeighborEntry>();
        foreach (Match m in WindowsArpLine().Matches(text))
            if (Ipv4.TryParse(m.Groups[1].Value, out var ip)) Add(list, ip, m.Groups[2].Value, null);
        return list;
    }

    /// <summary>macOS / BSD <c>arp -an</c>: "? (192.0.2.1) at aa:bb:cc:dd:ee:ff on en0 ...".</summary>
    public static List<NeighborEntry> ParseBsdArp(string text)
    {
        var list = new List<NeighborEntry>();
        foreach (Match m in BsdArpLine().Matches(text))
            if (Ipv4.TryParse(m.Groups[1].Value, out var ip)) Add(list, ip, m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : null);
        return list;
    }

    private static void Add(List<NeighborEntry> list, uint ip, string? mac, string? iface)
    {
        if (!Ipv4.IsProbeableUnicast(ip) || Ipv4.IsLoopback(ip)) return;
        var norm = mac?.Replace('-', ':').ToLowerInvariant();
        if (norm is "ff:ff:ff:ff:ff:ff" or "00:00:00:00:00:00") return;
        if (norm is not null && norm.StartsWith("01:00:5e")) return; // IPv4 multicast MAC
        if (list.Any(e => Ipv4.ToUInt(e.Address) == ip)) return;
        list.Add(new NeighborEntry(Ipv4.FromUInt(ip), norm, iface));
    }
}

/// <summary>Reads the real machine's adapters and neighbour table.</summary>
public sealed class SystemNetworkInventory : INetworkInventory
{
    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return list; }
        foreach (var nic in nics)
        {
            try
            {
                // Linux reports Unknown for some working links (bridges, tun); Windows uses Up.
                var up = nic.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown;
                var props = nic.GetIPProperties();
                int? index = null;
                try { index = props.GetIPv4Properties()?.Index; } catch { }
                var gw = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var prefix = ua.PrefixLength;
                    if (prefix is <= 0 or > 32)
                    {
                        try { prefix = Ipv4.PrefixFromMask(ua.IPv4Mask); } catch { prefix = 24; }
                        if (prefix <= 0) prefix = 24;
                    }
                    list.Add(new AdapterInfo(nic.Name, nic.Description ?? "", nic.NetworkInterfaceType, ua.Address, prefix, up, index, gw));
                }
            }
            catch { /* adapter vanished while enumerating */ }
        }
        return list;
    }

    public async Task<IReadOnlyList<NeighborEntry>> GetNeighborsAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                try { return WindowsNeighborTable.Read(); }
                catch { return NeighborParsers.ParseWindowsArp(await RunAsync("arp", "-a", ct).ConfigureAwait(false)); }
            }
            if (OperatingSystem.IsLinux() && File.Exists("/proc/net/arp"))
                return NeighborParsers.ParseProcNetArp(await File.ReadAllTextAsync("/proc/net/arp", ct).ConfigureAwait(false));
            return NeighborParsers.ParseBsdArp(await RunAsync("arp", "-an", ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<NeighborEntry>(); }
    }

    private static async Task<string> RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(); } catch { }
            return "";
        }
    }
}

/// <summary>GetIpNetTable2 (iphlpapi) reader. Row layout: MIB_IPNET_ROW2, 88 bytes on x64 and arm64.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsNeighborTable
{
    private const ushort AF_INET = 2;
    private const int RowSize = 88, TableOffset = 8;
    private const int OffAddrFamily = 0, OffAddrV4 = 4, OffPhys = 40, OffPhysLen = 72, OffState = 76;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpNetTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr memory);

    public static List<NeighborEntry> Read()
    {
        var rc = GetIpNetTable2(AF_INET, out var table);
        if (rc != 0) throw new InvalidOperationException($"GetIpNetTable2 failed ({rc})");
        var list = new List<NeighborEntry>();
        try
        {
            var count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                var row = table + TableOffset + i * RowSize;
                if ((ushort)Marshal.ReadInt16(row + OffAddrFamily) != AF_INET) continue;
                var state = Marshal.ReadInt32(row + OffState);
                if (state is 0 or 1) continue; // NlnsUnreachable, NlnsIncomplete
                var b = new byte[4];
                Marshal.Copy(row + OffAddrV4, b, 0, 4);
                var macLen = Math.Clamp(Marshal.ReadInt32(row + OffPhysLen), 0, 32);
                var mac = new byte[macLen];
                Marshal.Copy(row + OffPhys, mac, 0, macLen);
                var macText = macLen == 6 ? string.Join(":", mac.Select(x => x.ToString("x2"))) : null;
                var ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                if (!Ipv4.IsProbeableUnicast(ip) || Ipv4.IsLoopback(ip)) continue;
                if (macText is "ff:ff:ff:ff:ff:ff" or "00:00:00:00:00:00" || macText?.StartsWith("01:00:5e") == true) continue;
                list.Add(new NeighborEntry(new IPAddress(b), macText));
            }
        }
        finally { FreeMibTable(table); }
        return list;
    }
}

/// <summary>Fixed inventory, for tests and demo mode.</summary>
public sealed class StaticNetworkInventory : INetworkInventory
{
    public List<AdapterInfo> Adapters { get; } = new();
    public List<NeighborEntry> Neighbors { get; } = new();
    public IReadOnlyList<AdapterInfo> GetAdapters() => Adapters;
    public Task<IReadOnlyList<NeighborEntry>> GetNeighborsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<NeighborEntry>>(Neighbors);
}
