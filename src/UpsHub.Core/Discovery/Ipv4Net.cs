using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace UpsHub.Core.Discovery;

/// <summary>An IPv4 network in CIDR form. Network is the masked address as a host-order uint.</summary>
public readonly record struct Ipv4Subnet
{
    public uint Network { get; }
    public int Prefix { get; }

    public Ipv4Subnet(uint network, int prefix)
    {
        if (prefix is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefix));
        Prefix = prefix;
        Network = network & Ipv4.Mask(prefix);
    }

    public static Ipv4Subnet Of(IPAddress address, int prefix) => new(Ipv4.ToUInt(address), prefix);

    public uint Broadcast => Network | ~Ipv4.Mask(Prefix);
    public ulong Size => 1UL << (32 - Prefix);

    /// <summary>Usable host addresses: everything except network and broadcast, except /31 and /32 (RFC 3021).</summary>
    public ulong HostCount => Prefix >= 31 ? Size : Size - 2;

    public uint FirstHost => Prefix >= 31 ? Network : Network + 1;
    public uint LastHost => Prefix >= 31 ? Broadcast : Broadcast - 1;

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && Contains(Ipv4.ToUInt(address));

    public bool Contains(uint address) => (address & Ipv4.Mask(Prefix)) == Network;

    public IEnumerable<IPAddress> Hosts()
    {
        for (ulong a = FirstHost; a <= LastHost; a++) yield return Ipv4.FromUInt((uint)a);
    }

    public bool IsHostAddress(uint address) => Contains(address) && (Prefix >= 31 || (address != Network && address != Broadcast));

    public override string ToString() => $"{Ipv4.FromUInt(Network)}/{Prefix}";

    public static bool TryParse(string? text, out Ipv4Subnet subnet)
    {
        subnet = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('/');
        if (parts.Length != 2 || !Ipv4.TryParse(parts[0], out var addr)) return false;
        if (!int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32) return false;
        subnet = new Ipv4Subnet(addr, prefix);
        return true;
    }
}

public static class Ipv4
{
    public static uint Mask(int prefix) => prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

    public static uint ToUInt(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("Not an IPv4 address", nameof(address));
        Span<byte> b = stackalloc byte[4];
        address.TryWriteBytes(b, out _);
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static IPAddress FromUInt(uint a) => new(new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a });

    /// <summary>Strict dotted-quad parse (IPAddress.Parse also accepts "10" or "1.2").</summary>
    public static bool TryParse(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
        {
            if (p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit) || !int.TryParse(p, out var n) || n > 255) return false;
            value = value << 8 | (uint)n;
        }
        return true;
    }

    /// <summary>Converts a subnet mask such as 255.255.252.0 to a prefix length, or -1 if not contiguous.</summary>
    public static int PrefixFromMask(IPAddress mask)
    {
        var m = ToUInt(mask);
        var prefix = BitOperations.PopCount(m);
        return Mask(prefix) == m ? prefix : -1;
    }

    public static bool IsLoopback(uint a) => a >> 24 == 127;
    public static bool IsLinkLocal(uint a) => a >> 16 == 0xA9FE;           // 169.254/16 (APIPA)
    public static bool IsMulticast(uint a) => a >> 28 == 0xE;               // 224/4
    public static bool IsReservedOrBroadcast(uint a) => a >> 28 == 0xF || a == 0; // 240/4, 255.255.255.255, 0.0.0.0
    public static bool IsPrivate(uint a) =>
        a >> 24 == 10 || a >> 20 == (172 << 4 | 1) || a >> 16 == (192 << 8 | 168); // RFC 1918

    /// <summary>A plausible unicast host address to probe.</summary>
    public static bool IsProbeableUnicast(uint a) => !IsMulticast(a) && !IsReservedOrBroadcast(a);
}

/// <summary>A planned sweep of one network.</summary>
public sealed record ScanRange(Ipv4Subnet Subnet, string Label, string Reason)
{
    public ulong HostCount => Subnet.HostCount;
}

public static class ScanPlanner
{
    /// <summary>
    /// Full sweep for subnets up to <paramref name="maxFullPrefix"/> (default /22 = 1,022 hosts).
    /// Bigger networks only get the /24 around our own address (ARP hits cover the rest).
    /// </summary>
    public static ScanRange? Plan(AdapterInfo adapter, int maxFullPrefix = 22)
    {
        var addr = Ipv4.ToUInt(adapter.Address);
        if (adapter.Prefix >= 32) return null; // point-to-point / host route: nothing to sweep
        var label = adapter.Name;
        if (adapter.Prefix >= maxFullPrefix)
            return new ScanRange(new Ipv4Subnet(addr, adapter.Prefix), label, "whole subnet");
        var local = new Ipv4Subnet(addr, 24);
        return new ScanRange(local, label, $"/24 around this PC (network is /{adapter.Prefix})");
    }

    /// <summary>Merges overlapping ranges (two adapters on one LAN, or a /24 inside a /22).</summary>
    public static List<ScanRange> Dedupe(IEnumerable<ScanRange> ranges)
    {
        var result = new List<ScanRange>();
        foreach (var r in ranges.OrderBy(r => r.Subnet.Prefix))
        {
            if (result.Any(x => x.Subnet.Contains(r.Subnet.Network))) continue;
            result.Add(r);
        }
        return result;
    }
}

/// <summary>User-entered targets: "192.0.2.0/24", "10.0.0.5-10.0.0.40", "10.0.0.5-40", "10.0.0.9", "nas.local".</summary>
public sealed class TargetSpec
{
    public const int MaxAddresses = 65_536;
    public List<uint> Addresses { get; } = new();
    public List<string> HostNames { get; } = new();
    public List<string> Errors { get; } = new();

    public static TargetSpec Parse(string? text)
    {
        var spec = new TargetSpec();
        if (string.IsNullOrWhiteSpace(text)) return spec;
        var seen = new HashSet<uint>();
        foreach (var raw in text.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var item = raw.Trim();
            IEnumerable<uint> addrs;
            if (item.Contains('/'))
            {
                if (!Ipv4Subnet.TryParse(item, out var sn) || sn.Prefix < 16) { spec.Errors.Add($"{item}: use a subnet of /16 or smaller"); continue; }
                addrs = Range(sn.FirstHost, sn.LastHost);
            }
            else if (item.Contains('-') && Ipv4.TryParse(item.Split('-')[0], out var start))
            {
                var endText = item[(item.IndexOf('-') + 1)..];
                uint end;
                if (Ipv4.TryParse(endText, out var e)) end = e;
                else if (byte.TryParse(endText, out var lastOctet)) end = (start & 0xFFFFFF00) | lastOctet;
                else { spec.Errors.Add($"{item}: not a valid range"); continue; }
                if (end < start || end - start >= MaxAddresses) { spec.Errors.Add($"{item}: range is reversed or too large"); continue; }
                addrs = Range(start, end);
            }
            else if (Ipv4.TryParse(item, out var single)) addrs = new[] { single };
            else if (Uri.CheckHostName(item) is UriHostNameType.Dns or UriHostNameType.IPv6) { spec.HostNames.Add(item); continue; }
            else { spec.Errors.Add($"{item}: not an address, range or host name"); continue; }

            foreach (var a in addrs)
            {
                if (spec.Addresses.Count >= MaxAddresses) { spec.Errors.Add($"Stopped at {MaxAddresses:N0} addresses"); return spec; }
                if (seen.Add(a)) spec.Addresses.Add(a);
            }
        }
        return spec;
    }

    private static IEnumerable<uint> Range(uint a, uint b) { for (ulong x = a; x <= b; x++) yield return (uint)x; }

    /// <summary>Parses "3493, 3494" into a port list; always includes 3493 unless <paramref name="includeDefault"/> is false.</summary>
    public static List<int> ParsePorts(string? text, bool includeDefault = true)
    {
        var ports = new List<int>();
        if (includeDefault) ports.Add(3493);
        if (!string.IsNullOrWhiteSpace(text))
            foreach (var p in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(p, out var n) && n is > 0 and < 65536 && !ports.Contains(n)) ports.Add(n);
        return ports;
    }
}
