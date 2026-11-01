using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UpsHub.Core.Discovery;

/// <summary>A DNS-SD service instance found via mDNS.</summary>
public sealed record MdnsService(string Instance, string? Target, int Port, IReadOnlyList<IPAddress> Addresses);

public interface IMdnsBrowser
{
    /// <summary>Browses <paramref name="serviceType"/> (e.g. "_nut._tcp.local") and reports each instance as soon as it resolves.</summary>
    Task BrowseAsync(string serviceType, IEnumerable<IPAddress> interfaceAddresses, TimeSpan duration,
        Action<MdnsService> onFound, CancellationToken ct);
}

/// <summary>
/// Minimal DNS-SD browser. Sends one-shot "legacy unicast" PTR queries from an ephemeral port
/// (RFC 6762 §6.7), so it never needs to bind 5353 and coexists with Bonjour / the Windows mDNS stack.
/// </summary>
public sealed class MdnsBrowser : IMdnsBrowser
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    public async Task BrowseAsync(string serviceType, IEnumerable<IPAddress> interfaceAddresses, TimeSpan duration,
        Action<MdnsService> onFound, CancellationToken ct)
    {
        var query = DnsMessage.BuildQuery(serviceType, DnsMessage.TypePtr, unicastResponse: true);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = new MdnsAccumulator(serviceType);
        var sockets = new List<Socket>();
        try
        {
            foreach (var addr in interfaceAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct())
            {
                try
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.Bind(new IPEndPoint(addr, 0));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    sockets.Add(s);
                }
                catch (SocketException) { }
            }
            if (sockets.Count == 0) return;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(duration);
            var receivers = sockets.Select(s => ReceiveLoopAsync(s, state, reported, onFound, cts.Token)).ToList();
            // Query at t=0 and again at ~1 s (first packet is often lost while ARP/IGMP settle).
            for (int round = 0; round < 2 && !cts.IsCancellationRequested; round++)
            {
                foreach (var s in sockets)
                    try { await s.SendToAsync(query, SocketFlags.None, Group, cts.Token).ConfigureAwait(false); } catch { }
                try { await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            await Task.WhenAll(receivers).ConfigureAwait(false);
            // Instances whose SRV target had no A record in the reply: resolve by name (Windows 10+ resolves .local).
            foreach (var svc in state.Unresolved(reported))
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    rcts.CancelAfter(TimeSpan.FromSeconds(1.5));
                    var ips = await Dns.GetHostAddressesAsync(svc.Target!, rcts.Token).ConfigureAwait(false);
                    if (ips.Length > 0 && reported.Add(svc.Instance)) onFound(svc with { Addresses = ips });
                }
                catch { }
            }
        }
        finally { foreach (var s in sockets) s.Dispose(); }
    }

    private static async Task ReceiveLoopAsync(Socket s, MdnsAccumulator state, HashSet<string> reported,
        Action<MdnsService> onFound, CancellationToken ct)
    {
        var buf = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await s.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false); }
            catch { return; }
            var from = (r.RemoteEndPoint as IPEndPoint)?.Address;
            List<MdnsService> ready;
            lock (state)
            {
                if (!DnsMessage.TryParse(buf.AsSpan(0, r.ReceivedBytes), out var msg)) continue;
                ready = state.Add(msg, from);
            }
            foreach (var svc in ready)
            {
                bool isNew;
                lock (reported) isNew = reported.Add(svc.Instance);
                if (isNew) onFound(svc);
            }
        }
    }
}

/// <summary>Collects PTR/SRV/A records across packets and yields services once they have an address.</summary>
internal sealed class MdnsAccumulator
{
    private readonly string _type;
    private readonly HashSet<string> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Target, int Port)> _srv = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<IPAddress>> _addr = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _sender = new(StringComparer.OrdinalIgnoreCase);

    public MdnsAccumulator(string serviceType) => _type = serviceType.TrimEnd('.');

    public List<MdnsService> Add(DnsMessage msg, IPAddress? from)
    {
        foreach (var rr in msg.Records)
        {
            switch (rr.Type)
            {
                case DnsMessage.TypePtr when rr.Name.TrimEnd('.').Equals(_type, StringComparison.OrdinalIgnoreCase) && rr.Text is not null:
                    _instances.Add(rr.Text);
                    if (from is not null) _sender[rr.Text] = from;
                    break;
                case DnsMessage.TypeSrv when rr.Text is not null:
                    _srv[rr.Name] = (rr.Text, rr.Port);
                    break;
                case DnsMessage.TypeA or DnsMessage.TypeAaaa when rr.Address is not null:
                    if (!_addr.TryGetValue(rr.Name, out var set)) _addr[rr.Name] = set = new();
                    set.Add(rr.Address);
                    break;
            }
        }
        return Resolved().ToList();
    }

    private IEnumerable<MdnsService> Resolved()
    {
        foreach (var inst in _instances)
        {
            if (!_srv.TryGetValue(inst, out var srv)) continue;
            if (_addr.TryGetValue(srv.Target, out var ips) && ips.Count > 0)
                yield return new MdnsService(FriendlyInstance(inst), srv.Target.TrimEnd('.'), srv.Port,
                    ips.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList());
        }
    }

    public IEnumerable<MdnsService> Unresolved(HashSet<string> reported)
    {
        foreach (var inst in _instances)
        {
            var name = FriendlyInstance(inst);
            if (reported.Contains(name)) continue;
            if (_srv.TryGetValue(inst, out var srv))
            {
                // Fall back to the responder's own address if the name doesn't resolve.
                yield return new MdnsService(name, srv.Target.TrimEnd('.'), srv.Port,
                    _sender.TryGetValue(inst, out var s) ? new[] { s } : Array.Empty<IPAddress>());
            }
            else if (_sender.TryGetValue(inst, out var sender))
                yield return new MdnsService(name, null, 3493, new[] { sender });
        }
    }

    private string FriendlyInstance(string inst)
    {
        var suffix = "." + _type;
        var t = inst.TrimEnd('.');
        return t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? t[..^suffix.Length] : t;
    }
}

/// <summary>Just enough DNS wire format for mDNS: queries out, PTR/SRV/A/AAAA/TXT in (with name compression).</summary>
public sealed class DnsMessage
{
    public const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33;

    public sealed record Record(string Name, ushort Type, uint Ttl, string? Text = null, int Port = 0, IPAddress? Address = null);

    public ushort Id { get; private init; }
    public bool IsResponse { get; private init; }
    public List<Record> Records { get; } = new();

    public static byte[] BuildQuery(string name, ushort type, bool unicastResponse)
    {
        var buf = new List<byte>(64) { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }; // id 0, flags 0, QD=1
        WriteName(buf, name);
        buf.Add((byte)(type >> 8)); buf.Add((byte)type);
        ushort cls = (ushort)(1 | (unicastResponse ? 0x8000 : 0));
        buf.Add((byte)(cls >> 8)); buf.Add((byte)cls);
        return buf.ToArray();
    }

    /// <summary>Builds a response packet (used by tests and the demo responder).</summary>
    public static byte[] BuildResponse(IEnumerable<Record> answers)
    {
        var list = answers.ToList();
        var buf = new List<byte> { 0, 0, 0x84, 0, 0, 0, (byte)(list.Count >> 8), (byte)list.Count, 0, 0, 0, 0 };
        foreach (var r in list)
        {
            WriteName(buf, r.Name);
            buf.Add((byte)(r.Type >> 8)); buf.Add((byte)r.Type);
            buf.Add(0x80); buf.Add(1); // cache-flush + IN
            buf.Add((byte)(r.Ttl >> 24)); buf.Add((byte)(r.Ttl >> 16)); buf.Add((byte)(r.Ttl >> 8)); buf.Add((byte)r.Ttl);
            var data = new List<byte>();
            switch (r.Type)
            {
                case TypePtr: WriteName(data, r.Text!); break;
                case TypeSrv: data.AddRange(new byte[] { 0, 0, 0, 0, (byte)(r.Port >> 8), (byte)r.Port }); WriteName(data, r.Text!); break;
                case TypeA or TypeAaaa: data.AddRange(r.Address!.GetAddressBytes()); break;
                case TypeTxt: var t = Encoding.UTF8.GetBytes(r.Text ?? ""); data.Add((byte)t.Length); data.AddRange(t); break;
            }
            buf.Add((byte)(data.Count >> 8)); buf.Add((byte)data.Count);
            buf.AddRange(data);
        }
        return buf.ToArray();
    }

    private static void WriteName(List<byte> buf, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            if (b.Length is 0 or > 63) throw new ArgumentException("Bad DNS label", nameof(name));
            buf.Add((byte)b.Length); buf.AddRange(b);
        }
        buf.Add(0);
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out DnsMessage msg)
    {
        msg = null!;
        try { msg = Parse(data.ToArray()); return true; }
        catch { return false; }
    }

    private static DnsMessage Parse(byte[] d)
    {
        if (d.Length < 12) throw new FormatException("short");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(2));
        var msg = new DnsMessage { Id = BinaryPrimitives.ReadUInt16BigEndian(d), IsResponse = (flags & 0x8000) != 0 };
        int qd = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4));
        int rrCount = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8))
                      + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(10));
        int pos = 12;
        for (int i = 0; i < qd; i++) { ReadName(d, ref pos); pos += 4; }
        for (int i = 0; i < rrCount; i++)
        {
            var name = ReadName(d, ref pos);
            var type = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos));
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos + 4));
            int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 8));
            pos += 10;
            if (pos + len > d.Length) throw new FormatException("rdata");
            int rd = pos;
            Record? rec = type switch
            {
                TypePtr => new Record(name, type, ttl, Text: ReadName(d, ref rd)),
                TypeSrv => new Record(name, type, ttl, Port: BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 4)), Text: ReadNameAt(d, pos + 6)),
                TypeA when len == 4 => new Record(name, type, ttl, Address: new IPAddress(d.AsSpan(pos, 4))),
                TypeAaaa when len == 16 => new Record(name, type, ttl, Address: new IPAddress(d.AsSpan(pos, 16))),
                TypeTxt => new Record(name, type, ttl, Text: len > 0 ? Encoding.UTF8.GetString(d, pos + 1, Math.Min(d[pos], len - 1)) : ""),
                _ => null,
            };
            if (rec is not null) msg.Records.Add(rec);
            pos += len;
        }
        return msg;
    }

    private static string ReadNameAt(byte[] d, int pos) => ReadName(d, ref pos);

    private static string ReadName(byte[] d, ref int pos)
    {
        var sb = new StringBuilder();
        int p = pos, jumps = 0;
        bool jumped = false;
        while (true)
        {
            if (p >= d.Length) throw new FormatException("name");
            int len = d[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (++jumps > 32) throw new FormatException("loop");
                int target = ((len & 0x3F) << 8) | d[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = target;
                continue;
            }
            if (p + 1 + len > d.Length) throw new FormatException("label");
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(d, p + 1, len));
            p += 1 + len;
        }
        if (!jumped) pos = p;
        return sb.ToString();
    }
}
