using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UpsHub.FakeNut;

/// <summary>A fake UPS on the fake server. Variables can be changed while clients are connected.</summary>
public sealed class FakeUps
{
    public string Name { get; }
    public string Description { get; set; }
    public ConcurrentDictionary<string, string> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When true, LIST VAR / GET VAR answer ERR DATA-STALE.</summary>
    public bool Stale { get; set; }
    public bool DriverDisconnected { get; set; }

    public FakeUps(string name, string description = "")
    {
        Name = name;
        Description = description;
    }

    public FakeUps Set(string key, object value)
    {
        Vars[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return this;
    }

    /// <summary>A typical APC Back-UPS (usbhid-ups, 051d:0002) on mains.</summary>
    public static FakeUps ApcBackUps(string name = "apc") => new FakeUps(name, "APC Back-UPS")
        .Set("device.mfr", "American Power Conversion").Set("device.model", "Back-UPS XS 1400U")
        .Set("device.serial", "SYN0001400U").Set("device.type", "ups")
        .Set("driver.name", "usbhid-ups").Set("driver.version", "2.8.1")
        .Set("ups.vendorid", "051d").Set("ups.productid", "0002")
        .Set("ups.status", "OL").Set("ups.load", 23).Set("ups.realpower.nominal", 700)
        .Set("battery.charge", 100).Set("battery.charge.low", 10).Set("battery.runtime", 2460)
        .Set("battery.voltage", 27.1).Set("battery.voltage.nominal", 24.0)
        .Set("input.voltage", 231.0).Set("input.voltage.nominal", 230).Set("input.transfer.low", 155)
        .Set("input.transfer.high", 280).Set("output.voltage", 230.0).Set("output.frequency", 50.0);
}

public static class FakeUpsPresets
{
    public static FakeUps EatonEllipse(string name = "eaton") => new FakeUps(name, "Rack A")
        .Set("device.mfr", "EATON").Set("device.model", "Ellipse PRO 1200").Set("device.serial", "G355E21015")
        .Set("driver.name", "usbhid-ups").Set("ups.vendorid", "0463").Set("ups.productid", "ffff")
        .Set("ups.status", "OL CHRG").Set("ups.load", 31).Set("battery.charge", 87).Set("battery.runtime", 1630)
        .Set("input.voltage", 229.0).Set("output.voltage", 229.0);

    public static FakeUps CyberPower(string name = "cyberpower") => new FakeUps(name, "Network closet")
        .Set("device.mfr", "CPS").Set("device.model", "CP1500EPFCLCD").Set("device.serial", "CXXKV2004812")
        .Set("driver.name", "usbhid-ups").Set("ups.vendorid", "0764").Set("ups.productid", "0601")
        .Set("ups.status", "OB DISCHRG").Set("ups.load", 18).Set("battery.charge", 64).Set("battery.runtime", 1210)
        .Set("input.voltage", 0.0).Set("output.voltage", 230.0);
}

/// <summary>
/// Minimal in-process upsd speaking the NUT network protocol, for tests and the UI demo mode.
/// </summary>
public sealed class FakeNutServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private int _nextId;
    private Task? _acceptLoop;

    public ConcurrentDictionary<string, FakeUps> Ups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Version { get; set; } = "Network UPS Tools upsd 2.8.1 - https://www.networkupstools.org/";
    public string? RequiredUsername { get; set; }
    public string? RequiredPassword { get; set; }
    /// <summary>Simulate Synology-style ACCESS-DENIED for everything.</summary>
    public bool DenyAll { get; set; }
    /// <summary>Answer STARTTLS with OK (no real handshake follows; enough for capability probes).</summary>
    public bool StartTlsSupported { get; set; }
    public ConcurrentQueue<string> Received { get; } = new();
    public int ConnectionCount;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeNutServer(int port = 0, IPAddress? bind = null)
    {
        _listener = new TcpListener(bind ?? IPAddress.Loopback, port);
    }

    public FakeNutServer Add(FakeUps ups) { Ups[ups.Name] = ups; return this; }

    public FakeNutServer Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return this;
    }

    /// <summary>Abruptly closes every client socket (simulates a server restart / network drop).</summary>
    public void DropAllClients()
    {
        foreach (var (id, c) in _clients) { try { c.Client.Close(); } catch { } _clients.TryRemove(id, out _); }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            var id = Interlocked.Increment(ref _nextId);
            Interlocked.Increment(ref ConnectionCount);
            _clients[id] = c;
            _ = Task.Run(() => HandleAsync(id, c));
        }
    }

    private async Task HandleAsync(int id, TcpClient c)
    {
        string? user = null, pass = null;
        try
        {
            using var stream = c.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                Received.Enqueue(line.StartsWith("PASSWORD") ? "PASSWORD ***" : line);
                var parts = Split(line);
                if (parts.Count == 0) continue;
                var cmd = parts[0].ToUpperInvariant();
                bool authed = RequiredUsername is null || (user == RequiredUsername && pass == RequiredPassword);

                if (DenyAll && cmd is not ("VER" or "LOGOUT")) { await writer.WriteLineAsync("ERR ACCESS-DENIED"); continue; }

                switch (cmd)
                {
                    case "VER": await writer.WriteLineAsync(Version); break;
                    case "PROTVER" or "NETVER": await writer.WriteLineAsync("1.3"); break;
                    case "STARTTLS": await writer.WriteLineAsync(StartTlsSupported ? "OK STARTTLS" : "ERR FEATURE-NOT-CONFIGURED"); if (StartTlsSupported) return; break;
                    case "USERNAME":
                        if (user is not null) await writer.WriteLineAsync("ERR ALREADY-SET-USERNAME");
                        else { user = parts.ElementAtOrDefault(1); await writer.WriteLineAsync("OK"); }
                        break;
                    case "PASSWORD":
                        if (pass is not null) await writer.WriteLineAsync("ERR ALREADY-SET-PASSWORD");
                        else { pass = parts.ElementAtOrDefault(1); await writer.WriteLineAsync("OK"); }
                        break;
                    case "LOGIN":
                        if (RequiredUsername is not null && user is null) await writer.WriteLineAsync("ERR USERNAME-REQUIRED");
                        else if (!authed) await writer.WriteLineAsync("ERR ACCESS-DENIED");
                        else if (!Ups.ContainsKey(parts.ElementAtOrDefault(1) ?? "")) await writer.WriteLineAsync("ERR UNKNOWN-UPS");
                        else await writer.WriteLineAsync("OK");
                        break;
                    case "LOGOUT": await writer.WriteLineAsync("OK Goodbye"); return;
                    case "LIST":
                        if (!authed) { await writer.WriteLineAsync("ERR ACCESS-DENIED"); break; }
                        await HandleListAsync(writer, parts);
                        break;
                    case "GET":
                        if (!authed) { await writer.WriteLineAsync("ERR ACCESS-DENIED"); break; }
                        await HandleGetAsync(writer, parts);
                        break;
                    default: await writer.WriteLineAsync("ERR UNKNOWN-COMMAND"); break;
                }
            }
        }
        catch { /* client went away */ }
        finally { _clients.TryRemove(id, out _); c.Dispose(); }
    }

    private async Task HandleListAsync(StreamWriter w, List<string> p)
    {
        var what = p.ElementAtOrDefault(1)?.ToUpperInvariant();
        if (what == "UPS")
        {
            var sb = new StringBuilder("BEGIN LIST UPS\n");
            foreach (var u in Ups.Values.OrderBy(u => u.Name)) sb.Append($"UPS {u.Name} {Q(u.Description)}\n");
            sb.Append("END LIST UPS");
            await w.WriteLineAsync(sb.ToString());
            return;
        }
        if (what == "VAR")
        {
            var name = p.ElementAtOrDefault(2) ?? "";
            if (!Ups.TryGetValue(name, out var u)) { await w.WriteLineAsync("ERR UNKNOWN-UPS"); return; }
            if (u.DriverDisconnected) { await w.WriteLineAsync("ERR DRIVER-NOT-CONNECTED"); return; }
            if (u.Stale) { await w.WriteLineAsync("ERR DATA-STALE"); return; }
            var sb = new StringBuilder($"BEGIN LIST VAR {u.Name}\n");
            foreach (var (k, v) in u.Vars.OrderBy(kv => kv.Key)) sb.Append($"VAR {u.Name} {k} {Q(v)}\n");
            sb.Append($"END LIST VAR {u.Name}");
            await w.WriteLineAsync(sb.ToString());
            return;
        }
        await w.WriteLineAsync("ERR INVALID-ARGUMENT");
    }

    private async Task HandleGetAsync(StreamWriter w, List<string> p)
    {
        if (p.ElementAtOrDefault(1)?.ToUpperInvariant() != "VAR") { await w.WriteLineAsync("ERR INVALID-ARGUMENT"); return; }
        if (!Ups.TryGetValue(p.ElementAtOrDefault(2) ?? "", out var u)) { await w.WriteLineAsync("ERR UNKNOWN-UPS"); return; }
        if (u.Stale) { await w.WriteLineAsync("ERR DATA-STALE"); return; }
        var key = p.ElementAtOrDefault(3) ?? "";
        if (!u.Vars.TryGetValue(key, out var v)) { await w.WriteLineAsync("ERR VAR-NOT-SUPPORTED"); return; }
        await w.WriteLineAsync($"VAR {u.Name} {key} {Q(v)}");
    }

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static List<string> Split(string line)
    {
        var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q) { if (ch == '\\' && i + 1 < line.Length) sb.Append(line[++i]); else if (ch == '"') q = false; else sb.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ' ') { if (sb.Length > 0) { r.Add(sb.ToString()); sb.Clear(); } }
            else sb.Append(ch);
        }
        if (sb.Length > 0) r.Add(sb.ToString());
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        DropAllClients();
        if (_acceptLoop is not null) { try { await _acceptLoop; } catch { } }
        _cts.Dispose();
    }
}

/// <summary>Drives a FakeUps through a power outage: OL -> OB (draining) -> LB.</summary>
public static class OutageScenario
{
    /// <summary>Applies the state for step <paramref name="t"/> seconds into the outage (t &lt; 0 = mains OK).</summary>
    public static void Apply(FakeUps ups, double t)
    {
        if (t < 0)
        {
            ups.Set("ups.status", "OL").Set("battery.charge", 100).Set("battery.runtime", 2460)
               .Set("input.voltage", 231.0).Set("ups.load", 23);
            return;
        }
        var charge = Math.Max(4, 100 - t * 2.4);
        var runtime = Math.Max(30, 2460 * charge / 100.0 * 0.9);
        var status = charge <= 20 ? "OB DISCHRG LB" : "OB DISCHRG";
        ups.Set("ups.status", status).Set("battery.charge", Math.Round(charge))
           .Set("battery.runtime", Math.Round(runtime)).Set("input.voltage", 0)
           .Set("ups.load", 25).Set("battery.voltage", Math.Round(21.5 + charge * 0.055, 1));
    }
}
