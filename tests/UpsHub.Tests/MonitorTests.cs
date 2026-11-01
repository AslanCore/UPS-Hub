using System.Collections.Concurrent;
using UpsHub.Core.Model;
using UpsHub.Core.Monitoring;
using UpsHub.Core.Nut;
using UpsHub.Core.Settings;
using UpsHub.FakeNut;

namespace UpsHub.Tests;

public class MonitorTests
{
    private static ServerMonitor Monitor(FakeNutServer srv, ServerSettings? cfg = null, string? password = null)
    {
        cfg ??= new ServerSettings { Host = "127.0.0.1" };
        cfg.Port = srv.Port;
        return new ServerMonitor(cfg, password, TimeSpan.FromMilliseconds(100),
            backoff: new Backoff(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200)));
    }

    private static async Task<bool> Until(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    [Fact]
    public async Task Polls_every_ups_on_one_connection()
    {
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps("apc")).Add(new FakeUps("rack").Set("ups.status", "OB")).Start();
        await using var mon = Monitor(srv);
        var snaps = new ConcurrentDictionary<string, UpsSnapshot>();
        mon.SnapshotUpdated += (a, s) => snaps[a.Ups] = s;
        mon.Start();
        Assert.True(await Until(() => snaps.Count == 2));
        Assert.Equal(ConnectionState.Connected, mon.Status.State);
        Assert.Contains("2.8.1", mon.Status.ServerVersion);
        Assert.Equal(UpsHealth.OnBattery, snaps["rack"].Health);
        Assert.Equal(1, srv.ConnectionCount);
    }

    [Fact]
    public async Task Reconnects_after_drop_and_marks_stale_meanwhile()
    {
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps("apc")).Start();
        await using var mon = Monitor(srv);
        int stale = 0, fresh = 0;
        mon.SnapshotUpdated += (_, s) => { if (s.Stale) Interlocked.Increment(ref stale); else Interlocked.Increment(ref fresh); };
        mon.Start();
        // Wait for real data first: with nothing known yet there is nothing to mark stale.
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Connected && Volatile.Read(ref fresh) >= 1));
        srv.DropAllClients();
        Assert.True(await Until(() => srv.ConnectionCount >= 2 && mon.Status.State == ConnectionState.Connected));
        Assert.True(await Until(() => Volatile.Read(ref stale) >= 1));
    }

    [Fact]
    public async Task Keeps_retrying_while_server_is_down()
    {
        // Reserve a port, start with nothing listening, then bring the server up on it.
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var cfg = new ServerSettings { Host = "127.0.0.1", Port = port };
        await using var mon = new ServerMonitor(cfg, null, TimeSpan.FromMilliseconds(100),
            backoff: new Backoff(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150)));
        mon.Start();
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Reconnecting && mon.Status.Attempt >= 3));
        await using var srv = new FakeNutServer(port).Add(FakeUps.ApcBackUps()).Start();
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Connected));
    }

    [Fact]
    public async Task Data_stale_publishes_stale_snapshot_without_dropping()
    {
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps("apc")).Start();
        await using var mon = Monitor(srv);
        UpsSnapshot? last = null;
        mon.SnapshotUpdated += (_, s) => last = s;
        mon.Start();
        Assert.True(await Until(() => last is { Stale: false }));
        srv.Ups["apc"].Stale = true;
        Assert.True(await Until(() => last is { Stale: true }));
        Assert.Equal(ConnectionState.Connected, mon.Status.State);
        Assert.Contains("DATA-STALE", mon.Status.Message);
        Assert.Equal(1, srv.ConnectionCount);
    }

    [Fact]
    public async Task Access_denied_reports_friendly_error_and_retries()
    {
        await using var srv = new FakeNutServer { DenyAll = true }.Add(FakeUps.ApcBackUps()).Start();
        await using var mon = Monitor(srv);
        mon.Start();
        Assert.True(await Until(() => mon.Status.LastError == NutError.AccessDenied && mon.Status.Attempt >= 2));
        Assert.Contains("upsd.users", mon.Status.Message);
        srv.DenyAll = false;
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Connected));
    }

    [Fact]
    public async Task Configured_names_and_login_are_used()
    {
        await using var srv = new FakeNutServer { RequiredUsername = "u", RequiredPassword = "p" }
            .Add(FakeUps.ApcBackUps("apc")).Add(new FakeUps("other").Set("ups.status", "OL")).Start();
        var cfg = new ServerSettings { Host = "127.0.0.1", Username = "u", UpsNames = { "apc" }, LoginAsSecondary = true };
        await using var mon = Monitor(srv, cfg, "p");
        var seen = new ConcurrentBag<string>();
        mon.SnapshotUpdated += (a, _) => seen.Add(a.Ups);
        mon.Start();
        Assert.True(await Until(() => seen.Count >= 2));
        Assert.All(seen, n => Assert.Equal("apc", n));
        Assert.Contains("LOGIN apc", srv.Received);
        Assert.Equal(2, mon.KnownUps.Count);
    }

    [Fact]
    public async Task Kick_with_reconnect_opens_a_new_connection()
    {
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps()).Start();
        await using var mon = Monitor(srv);
        mon.Start();
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Connected));
        mon.Kick(reconnect: true);
        Assert.True(await Until(() => srv.ConnectionCount >= 2));
        Assert.Contains("LOGOUT", srv.Received);
    }
}
