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

public class ReconnectTests
{
    private static ServerMonitor Monitor(FakeNutServer srv, Backoff? b = null)
    {
        var cfg = new ServerSettings { Host = "127.0.0.1", Port = srv.Port };
        return new ServerMonitor(cfg, null, TimeSpan.FromMilliseconds(100),
            backoff: b ?? new Backoff(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200)));
    }

    private static async Task<bool> Until(Func<bool> cond, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    [Fact]
    public async Task Hung_server_keeps_retrying_and_recovers()
    {
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps()).Start();
        await using var mon = Monitor(srv);
        mon.Start();
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Connected));
        srv.Frozen = true;                       // upsd stops answering, sockets stay open
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Reconnecting && mon.Status.Attempt >= 3, 20000), mon.Status.ToString());
        srv.Frozen = false;
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Connected));
        Assert.Equal(0, mon.Status.Attempt);
    }
}

public class ReconnectKickTests
{
    private static async Task<bool> Until(Func<bool> cond, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    [Fact]
    public async Task Kick_cancels_a_long_backoff_and_reconnects_immediately()
    {
        // Backoff of 30 s: without the kick the server would not be tried again during this test.
        var port = FreePort();
        var cfg = new ServerSettings { Host = "127.0.0.1", Port = port };
        await using var mon = new ServerMonitor(cfg, null, TimeSpan.FromMilliseconds(100),
            backoff: new Backoff(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)));
        mon.Start();
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Reconnecting && mon.Status.NextRetry is not null));
        Assert.True(mon.Status.NextRetry > DateTimeOffset.UtcNow.AddSeconds(15));
        await using var srv = new FakeNutServer(port).Add(FakeUps.ApcBackUps()).Start();
        mon.Kick(reconnect: true);
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Connected, 3000));
    }

    [Fact]
    public async Task Kick_interrupts_a_hung_connection()
    {
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps()).Start();
        var cfg = new ServerSettings { Host = "127.0.0.1", Port = srv.Port };
        // 5 s poll => 10 s command timeout: a hung upsd would block for 10 s without the kick.
        await using var mon = new ServerMonitor(cfg, null, TimeSpan.FromSeconds(5));
        mon.Start();
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Connected));
        srv.Frozen = true;
        mon.Kick();                  // poll now: the command hangs
        await Task.Delay(300);
        srv.Frozen = false;
        var before = srv.ConnectionCount;
        mon.Kick(reconnect: true);
        Assert.True(await Until(() => srv.ConnectionCount > before && mon.Status.State == ConnectionState.Connected, 3000));
    }

    [Fact]
    public async Task Dns_failure_keeps_retrying_with_attempt_count()
    {
        var cfg = new ServerSettings { Host = "nonexistent-host.invalid", Port = 3493 };
        await using var mon = new ServerMonitor(cfg, null, TimeSpan.FromMilliseconds(100),
            backoff: new Backoff(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50)));
        mon.Start();
        Assert.True(await Until(() => mon.Status.Attempt >= 3, 30000), mon.Status.ToString());
        Assert.Equal(ConnectionState.Reconnecting, mon.Status.State);
    }

    [Fact]
    public async Task Unexpected_exception_does_not_kill_the_loop()
    {
        int calls = 0;
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps()).Start();
        var cfg = new ServerSettings { Host = "127.0.0.1", Port = srv.Port };
        await using var mon = new ServerMonitor(cfg, null, TimeSpan.FromMilliseconds(100),
            clientFactory: o => Interlocked.Increment(ref calls) <= 2 ? throw new InvalidOperationException("boom") : new NutClient(o),
            backoff: new Backoff(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50)));
        mon.Start();
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Connected));
    }

    [Fact]
    public void Backoff_caps_at_30_seconds_and_resets()
    {
        var b = new Backoff(seed: 1);
        TimeSpan last = default;
        for (int i = 0; i < 50; i++) last = b.Next();
        Assert.True(last <= TimeSpan.FromSeconds(30));
        Assert.True(last >= TimeSpan.FromSeconds(20));
        Assert.Equal(50, b.Attempt);
        b.Reset();
        Assert.Equal(0, b.Attempt);
        Assert.True(b.Next() <= TimeSpan.FromSeconds(1.2));
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
}
