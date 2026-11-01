using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UpsHub.Core.Discovery;

/// <summary>
/// Finds NUT servers (upsd) on any network: localhost, saved hosts, mDNS _nut._tcp, ARP neighbours,
/// then a sweep of each active adapter's subnet (/22 cap). Every open port is confirmed with VER + LIST UPS.
/// </summary>
public sealed class NutDiscovery
{
    public const string MdnsServiceType = "_nut._tcp.local";

    private readonly INetworkInventory _inventory;
    private readonly IMdnsBrowser? _mdns;

    public NutDiscovery(INetworkInventory? inventory = null, IMdnsBrowser? mdns = null)
    {
        _inventory = inventory ?? new SystemNetworkInventory();
        _mdns = mdns;
    }

    public static NutDiscovery ForThisMachine() => new(new SystemNetworkInventory(), new MdnsBrowser());

    private sealed record Target(IPAddress Ip, int Port, DiscoverySource Source, bool Priority, string? HostName = null, string? MdnsInstance = null);

    public async Task<DiscoveryResult> RunAsync(DiscoveryOptions o, IProgress<DiscoveryProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var ports = o.Ports.Count > 0 ? o.Ports.Distinct().ToList() : new List<int> { 3493 };

        // ---- Adapters and plan -----------------------------------------------------------
        var all = _inventory.GetAdapters();
        var usable = new List<AdapterInfo>();
        var skipped = new List<AdapterInfo>();
        foreach (var a in all)
        {
            if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            var u = Ipv4.ToUInt(a.Address);
            bool skip = !a.IsUp || (a.IsLoopback && !o.IncludeLoopbackAdapters) || Ipv4.IsLinkLocal(u) || (a.IsVirtual && !o.IncludeVirtualAdapters);
            (skip ? skipped : usable).Add(a);
        }
        var localAddresses = new HashSet<IPAddress>(all.Where(a => !a.IsLoopback).Select(a => a.Address)) { IPAddress.Loopback, IPAddress.IPv6Loopback };
        var ranges = o.ScanSubnets
            ? ScanPlanner.Dedupe(usable.Select(a => ScanPlanner.Plan(a, o.MaxFullScanPrefix)).OfType<ScanRange>())
            : new List<ScanRange>();
        var excludedSubnets = skipped.Where(a => !a.IsLoopback).Select(a => a.Subnet).ToList();
        if (usable.Count == 0 && o.ScanSubnets) notes.Add("No active network adapter to scan (only loopback, virtual or disconnected ones).");

        // ---- Queue ----------------------------------------------------------------------
        var seen = new ConcurrentDictionary<(uint, int), byte>();
        var seenV6 = new ConcurrentDictionary<string, byte>();
        var prio = new ConcurrentQueue<Target>();
        var normal = new ConcurrentQueue<Target>();
        var signal = new SemaphoreSlim(0);
        int total = 0, probed = 0, pendingProducers = 1;
        string? current = null;

        bool Enqueue(Target t)
        {
            if (t.Ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (!seen.TryAdd((Ipv4.ToUInt(t.Ip), t.Port), 0)) return false;
            }
            else if (!seenV6.TryAdd($"{t.Ip}:{t.Port}", 0)) return false;
            Interlocked.Increment(ref total);
            (t.Priority ? prio : normal).Enqueue(t);
            signal.Release();
            return true;
        }

        void ProducerDone()
        {
            if (Interlocked.Decrement(ref pendingProducers) == 0) signal.Release(o.Concurrency + 1);
        }

        // ---- Results ---------------------------------------------------------------------
        var servers = new List<DiscoveredServer>();
        var gate = new object();
        IReadOnlyList<DiscoveredServer> Snapshot() { lock (gate) return servers.Select(s => s.Clone()).ToList(); }

        void AddOrMerge(DiscoveredServer s)
        {
            lock (gate)
            {
                var match = servers.FirstOrDefault(x => SameServer(x, s));
                if (match is null) { servers.Add(s); return; }
                Merge(match, s);
            }
        }

        // ---- Priority sources ------------------------------------------------------------
        if (o.IncludeLocalhost)
            foreach (var p in ports)
            {
                Enqueue(new Target(IPAddress.Loopback, p, DiscoverySource.Localhost, true));
                Enqueue(new Target(IPAddress.IPv6Loopback, p, DiscoverySource.Localhost, true));
            }

        var hostNameJobs = new List<(string Host, int? Port, DiscoverySource Source)>();
        foreach (var h in o.SavedHosts.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var (host, port) = SplitHostPort(h.Trim());
            hostNameJobs.Add((host, port, DiscoverySource.Saved));
        }
        var custom = TargetSpec.Parse(o.CustomTargets);
        notes.AddRange(custom.Errors);
        foreach (var h in custom.HostNames) hostNameJobs.Add((h, null, DiscoverySource.Custom));

        foreach (var (host, port, source) in hostNameJobs)
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                foreach (var p in port is { } pp ? new List<int> { pp } : ports) Enqueue(new Target(ip, p, source, true));
                continue;
            }
            Interlocked.Increment(ref pendingProducers);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    rcts.CancelAfter(TimeSpan.FromSeconds(2));
                    var ips = await Dns.GetHostAddressesAsync(host, rcts.Token).ConfigureAwait(false);
                    var pick = ips.Where(i => i.AddressFamily == AddressFamily.InterNetwork).ToList();
                    if (pick.Count == 0) pick = ips.Take(1).ToList();
                    foreach (var ip2 in pick)
                        foreach (var p in port is { } pp ? new List<int> { pp } : ports)
                            Enqueue(new Target(ip2, p, source, true, HostName: host));
                }
                catch (Exception) when (!ct.IsCancellationRequested) { lock (notes) notes.Add($"Couldn't resolve {host}."); }
                catch { }
                finally { ProducerDone(); }
            }, CancellationToken.None);
        }

        foreach (var a in custom.Addresses)
            foreach (var p in ports) Enqueue(new Target(Ipv4.FromUInt(a), p, DiscoverySource.Custom, false));

        // mDNS can name a host we already queued from the sweep: remember its name for when it is confirmed.
        var mdnsNames = new ConcurrentDictionary<(string, int), (string? Host, string Instance)>();
        void MarkMdns(Target t)
        {
            mdnsNames[(t.Ip.ToString(), t.Port)] = (t.HostName, t.MdnsInstance!);
            lock (gate)
                foreach (var s in servers.Where(s => s.Port == t.Port && s.Addresses.Contains(t.Ip)))
                { s.Sources |= DiscoverySource.Mdns; s.MdnsInstance ??= t.MdnsInstance; s.HostName ??= t.HostName; }
        }

        int mdnsCount = 0;
        if (o.UseMdns && _mdns is not null)
        {
            Interlocked.Increment(ref pendingProducers);
            var ifaces = usable.Select(a => a.Address).ToList();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _mdns.BrowseAsync(MdnsServiceType, ifaces, o.MdnsDuration, svc =>
                    {
                        Interlocked.Increment(ref mdnsCount);
                        foreach (var ip in svc.Addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).DefaultIfEmpty(svc.Addresses.FirstOrDefault()))
                        {
                            if (ip is null) continue;
                            var t = new Target(ip, svc.Port > 0 ? svc.Port : 3493, DiscoverySource.Mdns, true, svc.Target, svc.Instance);
                            if (!Enqueue(t)) MarkMdns(t);
                        }
                    }, ct).ConfigureAwait(false);
                }
                catch { }
                finally { ProducerDone(); }
            }, CancellationToken.None);
        }

        int neighborCount = 0;
        if (o.UseNeighbors)
        {
            try
            {
                var neighbors = await _inventory.GetNeighborsAsync(ct).ConfigureAwait(false);
                foreach (var n in neighbors)
                {
                    if (n.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var u = Ipv4.ToUInt(n.Address);
                    if (excludedSubnets.Any(sn => sn.Contains(u)) && !usable.Any(a => a.Subnet.Contains(u))) continue;
                    if (usable.Any(a => a.Subnet.Contains(u) && !a.Subnet.IsHostAddress(u))) continue; // subnet broadcast
                    neighborCount++;
                    foreach (var p in ports) Enqueue(new Target(n.Address, p, DiscoverySource.Neighbor, false));
                }
            }
            catch (OperationCanceledException) { }
        }

        foreach (var r in ranges)
            foreach (var ip in r.Subnet.Hosts())
                foreach (var p in ports) Enqueue(new Target(ip, p, DiscoverySource.Subnet, false));
        ProducerDone();

        // ---- Workers ---------------------------------------------------------------------
        using var reportCts = new CancellationTokenSource();
        var reporter = progress is null ? Task.CompletedTask : Task.Run(async () =>
        {
            while (!reportCts.IsCancellationRequested)
            {
                progress!.Report(new DiscoveryProgress(DiscoveryPhase.Scanning, Volatile.Read(ref probed), Volatile.Read(ref total), current, Snapshot()));
                try { await Task.Delay(120, reportCts.Token).ConfigureAwait(false); } catch { }
            }
        });
        progress?.Report(new DiscoveryProgress(DiscoveryPhase.Preparing, 0, total, null, Array.Empty<DiscoveredServer>()));

        async Task Worker()
        {
            while (!ct.IsCancellationRequested)
            {
                if (!prio.TryDequeue(out var t) && !normal.TryDequeue(out t))
                {
                    if (Volatile.Read(ref pendingProducers) == 0 && prio.IsEmpty && normal.IsEmpty) return;
                    try { await signal.WaitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                    continue;
                }
                current = t.Ip.ToString();
                try { await ProbeOneAsync(t).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch { /* one bad host must not stop the scan */ }
                finally { Interlocked.Increment(ref probed); }
            }
        }

        async Task ProbeOneAsync(Target t)
        {
            if (o.SimulatedLatency > TimeSpan.Zero) await Task.Delay(o.SimulatedLatency, ct).ConfigureAwait(false);
            var open = await NutProbe.TryConnectAsync(t.Ip, t.Port, t.Priority ? o.PriorityConnectTimeout : o.ConnectTimeout, ct).ConfigureAwait(false);
            if (open is null) return;
            var host = t.Ip.AddressFamily == AddressFamily.InterNetworkV6 ? t.Ip.ToString() : t.Ip.ToString();
            var s = await NutProbe.ConfirmAsync(host, t.Port, o.ConfirmTimeout, o.CheckTls, ct).ConfigureAwait(false);
            if (s is null) return;
            s.Latency = open.Value;
            s.Addresses.Add(t.Ip);
            s.Sources = t.Source;
            s.IsLocalMachine = localAddresses.Contains(t.Ip);
            s.HostName = t.HostName;
            s.MdnsInstance = t.MdnsInstance;
            if (t.Source == DiscoverySource.Saved && t.HostName is not null) s.Host = t.HostName;
            if (mdnsNames.TryGetValue((t.Ip.ToString(), t.Port), out var m))
            { s.Sources |= DiscoverySource.Mdns; s.MdnsInstance ??= m.Instance; s.HostName ??= m.Host; }
            if (o.ResolveHostNames && s.HostName is null && !s.IsLocalMachine)
                s.HostName = await ReverseLookupAsync(t.Ip, ct).ConfigureAwait(false);
            if (s.IsLocalMachine) s.HostName ??= "This PC";
            AddOrMerge(s);
        }

        var workers = Enumerable.Range(0, Math.Max(1, o.Concurrency)).Select(_ => Task.Run(Worker, CancellationToken.None)).ToList();
        await Task.WhenAll(workers).ConfigureAwait(false);
        reportCts.Cancel();
        await reporter.ConfigureAwait(false);

        var cancelled = ct.IsCancellationRequested;
        var final = Snapshot().OrderBy(s => s.IsLocalMachine ? 1 : 0).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
        progress?.Report(new DiscoveryProgress(cancelled ? DiscoveryPhase.Cancelled : DiscoveryPhase.Done, probed, total, null, final));
        return new DiscoveryResult(final, ranges, skipped, neighborCount, mdnsCount, probed, sw.Elapsed, cancelled, notes);
    }

    // ---- Dedupe --------------------------------------------------------------------------

    /// <summary>
    /// Same upsd reached on several addresses? Only merge when the static fingerprint (port, version, UPS list)
    /// matches AND there is real evidence it's one box: same serials, same host name / mDNS instance, or both are this PC.
    /// Two identical Raspberry Pis with an "ups" each stay separate.
    /// </summary>
    internal static bool SameServer(DiscoveredServer a, DiscoveredServer b)
    {
        if (a.Port != b.Port) return false;
        if (a.Addresses.Intersect(b.Addresses).Any()) return true;
        if (a.Fingerprint != b.Fingerprint) return false;
        if (a.IsLocalMachine && b.IsLocalMachine) return true;
        if (a.SerialSet.Length > 0 && a.SerialSet == b.SerialSet) return true;
        if (a.MdnsInstance is not null && string.Equals(a.MdnsInstance, b.MdnsInstance, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.HostName is not null && !a.IsLocalMachine && string.Equals(a.HostName, b.HostName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static void Merge(DiscoveredServer into, DiscoveredServer other)
    {
        foreach (var ip in other.Addresses) if (!into.Addresses.Contains(ip)) into.Addresses.Add(ip);
        into.Sources |= other.Sources;
        into.MdnsInstance ??= other.MdnsInstance;
        if (into.HostName is null or "This PC" && other.HostName is not null and not "This PC") into.HostName = other.HostName;
        into.HostName ??= other.HostName;
        into.TlsSupported ??= other.TlsSupported;
        if (other.Latency < into.Latency) into.Latency = other.Latency;
        into.IsLocalMachine |= other.IsLocalMachine;
        into.Host = PickHost(into, other);
    }

    /// <summary>Preferred connect address: saved host name, then 127.0.0.1 for this PC, then private IPv4, then anything.</summary>
    private static string PickHost(DiscoveredServer a, DiscoveredServer b)
    {
        foreach (var s in new[] { a, b })
            if (s.Sources.HasFlag(DiscoverySource.Saved) && !IPAddress.TryParse(s.Host, out _)) return s.Host;
        var ips = a.Addresses;
        if (a.IsLocalMachine && ips.Any(i => i.Equals(IPAddress.Loopback))) return "127.0.0.1";
        var v4 = ips.Where(i => i.AddressFamily == AddressFamily.InterNetwork).ToList();
        var best = v4.FirstOrDefault(i => Ipv4.IsPrivate(Ipv4.ToUInt(i)) && !IPAddress.IsLoopback(i))
                   ?? v4.FirstOrDefault(i => !IPAddress.IsLoopback(i)) ?? v4.FirstOrDefault() ?? ips.FirstOrDefault();
        return best?.ToString() ?? a.Host;
    }

    private static async Task<string?> ReverseLookupAsync(IPAddress ip, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), cts.Token).ConfigureAwait(false);
            var name = entry.HostName;
            return string.IsNullOrWhiteSpace(name) || name == ip.ToString() ? null : name;
        }
        catch { return null; }
    }

    internal static (string Host, int? Port) SplitHostPort(string s)
    {
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            if (end > 0)
                return (s[1..end], end + 2 < s.Length && s[end + 1] == ':' && int.TryParse(s[(end + 2)..], out var p6) ? p6 : null);
        }
        var colon = s.LastIndexOf(':');
        if (colon > 0 && s.IndexOf(':') == colon && int.TryParse(s[(colon + 1)..], out var p)) return (s[..colon], p);
        return (s, null);
    }
}
