using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using UpsHub.Core.Nut;

namespace UpsHub.Core.Discovery;

public static class NutProbe
{
    /// <summary>TCP connect check. Returns the connect time, or null if closed / filtered / timed out.</summary>
    public static async Task<TimeSpan?> TryConnectAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await s.ConnectAsync(new IPEndPoint(ip, port), cts.Token).ConfigureAwait(false);
            var elapsed = sw.Elapsed;
            try { s.Shutdown(SocketShutdown.Both); } catch { }
            return elapsed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }

    private static readonly string[] InfoVars =
        { "device.mfr", "device.model", "ups.mfr", "ups.model", "ups.status", "battery.charge", "device.serial", "ups.serial" };

    /// <summary>
    /// Confirms upsd with VER + LIST UPS, then reads a few variables per UPS and checks STARTTLS.
    /// Returns null when whatever is listening doesn't speak NUT.
    /// </summary>
    public static async Task<DiscoveredServer?> ConfirmAsync(string host, int port, TimeSpan timeout, bool checkTls, CancellationToken ct)
    {
        await using var c = new NutClient(new NutConnectOptions
        {
            Host = host, Port = port, ConnectTimeout = timeout, CommandTimeout = timeout, Tls = TlsMode.Off,
        });
        var sw = Stopwatch.StartNew();
        try { await c.ConnectAsync(ct).ConfigureAwait(false); }
        catch (NutConnectionException) { return null; }
        var latency = sw.Elapsed;

        var server = new DiscoveredServer { Host = host, Port = port, Latency = latency };
        bool speaksNut = false;
        try
        {
            try
            {
                server.Version = await c.GetServerVersionAsync(ct).ConfigureAwait(false);
                speaksNut = LooksLikeNutVersion(server.Version);
            }
            catch (NutException ex) { speaksNut = ex.Error != NutError.Unknown; }

            try
            {
                var list = await c.ListUpsAsync(ct).ConfigureAwait(false);
                speaksNut = true;
                foreach (var u in list.Take(32))
                    server.Ups.Add(await ReadUpsAsync(c, u, server, ct).ConfigureAwait(false));
            }
            catch (NutException ex)
            {
                speaksNut |= ex.Error != NutError.Unknown;
                if (ex.Error is NutError.AccessDenied or NutError.UsernameRequired or NutError.PasswordRequired) server.AuthRequired = true;
            }

            if (!speaksNut) return null;

            if (checkTls)
            {
                // Last, because after "OK STARTTLS" this connection expects a TLS handshake.
                try
                {
                    var reply = await c.SendAsync("STARTTLS", ct).ConfigureAwait(false);
                    server.TlsSupported = reply.StartsWith("OK", StringComparison.Ordinal);
                }
                catch (NutException) { server.TlsSupported = false; }
            }
            else
            {
                try { await c.LogoutAsync(ct).ConfigureAwait(false); } catch { }
            }
        }
        catch (NutConnectionException) when (!speaksNut) { return null; }
        catch (NutConnectionException) { /* dropped mid-way: keep what we have */ }
        return server;
    }

    private static async Task<DiscoveredUps> ReadUpsAsync(NutClient c, NutUpsInfo info, DiscoveredServer server, CancellationToken ct)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in InfoVars)
        {
            if (name is "ups.mfr" && v.ContainsKey("device.mfr")) continue;
            if (name is "ups.model" && v.ContainsKey("device.model")) continue;
            if (name is "ups.serial" && v.ContainsKey("device.serial")) continue;
            try { v[name] = await c.GetVarAsync(info.Name, name, ct).ConfigureAwait(false); }
            catch (NutException ex)
            {
                if (ex.Error == NutError.AccessDenied) { server.AuthRequired = true; break; }
                if (ex.Error is NutError.DataStale or NutError.DriverNotConnected) break;
            }
        }
        string? Get(params string[] keys) => keys.Select(k => v.GetValueOrDefault(k)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
        double? charge = double.TryParse(Get("battery.charge"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ch) ? ch : null;
        return new DiscoveredUps(info.Name, info.Description, Get("device.mfr", "ups.mfr"), Get("device.model", "ups.model"),
            Get("ups.status"), charge, Get("device.serial", "ups.serial"));
    }

    private static bool LooksLikeNutVersion(string? v) =>
        v is not null && (v.Contains("upsd", StringComparison.OrdinalIgnoreCase)
                          || v.Contains("Network UPS Tools", StringComparison.OrdinalIgnoreCase)
                          || v.StartsWith("NUT", StringComparison.OrdinalIgnoreCase));
}
