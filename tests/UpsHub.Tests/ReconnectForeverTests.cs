using System.Net;
using System.Net.Sockets;
using System.Text;
using UpsHub.Core.Monitoring;
using UpsHub.Core.Nut;
using UpsHub.Core.Settings;
using UpsHub.FakeNut;

namespace UpsHub.Tests;

/// <summary>The monitor must never stop retrying, whatever the server does (user report, Nov 2026).</summary>
public class ReconnectForeverTests
{
    private static async Task<bool> Until(Func<bool> cond, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private static ServerMonitor Mon(int port, TlsMode tls = TlsMode.Off) =>
        new(new ServerSettings { Host = "127.0.0.1", Port = port, Tls = tls }, null, TimeSpan.FromMilliseconds(100),
            backoff: new Backoff(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(80)));

    /// <summary>A server that misbehaves in one of several ways on every connection.</summary>
    private sealed class Hostile : IAsyncDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public int Connections;
        public Hostile(Func<NetworkStream, CancellationToken, Task> behave)
        {
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                    Interlocked.Increment(ref Connections);
                    _ = Task.Run(async () => { try { using (c) await behave(c.GetStream(), _cts.Token); } catch { } });
                }
            });
        }
        public ValueTask DisposeAsync() { _cts.Cancel(); _l.Stop(); return ValueTask.CompletedTask; }
    }

    private static async Task Reply(NetworkStream s, string text, CancellationToken ct)
    {
        var buf = new byte[256];
        await s.ReadAsync(buf, ct);
        await s.WriteAsync(Encoding.ASCII.GetBytes(text), ct);
    }

    public static IEnumerable<object[]> Behaviours() => new[]
    {
        new object[] { "silent", TlsMode.Off },
        new object[] { "close", TlsMode.Off },
        new object[] { "garbage", TlsMode.Off },
        new object[] { "badlist", TlsMode.Off },
        new object[] { "silent", TlsMode.Require },
        new object[] { "tls-ok-then-silent", TlsMode.Require },
        new object[] { "tls-ok-then-silent", TlsMode.Prefer },
    };

    [Theory]
    [MemberData(nameof(Behaviours))]
    public async Task Keeps_retrying_against_a_misbehaving_server(string how, TlsMode tls)
    {
        Func<NetworkStream, CancellationToken, Task> behave = how switch
        {
            "silent" => async (s, ct) => await Task.Delay(Timeout.Infinite, ct),
            "close" => (s, ct) => Task.CompletedTask,
            "garbage" => async (s, ct) => { while (true) await Reply(s, "\u00ff\u00fe nonsense\n", ct); },
            "badlist" => async (s, ct) =>
            {
                // Valid version/USERNAME replies, then a LIST UPS that never ends properly.
                while (true) await Reply(s, "BEGIN LIST UPS\nUPS\n", ct);
            },
            "tls-ok-then-silent" => async (s, ct) => { await Reply(s, "OK STARTTLS\n", ct); await Task.Delay(Timeout.Infinite, ct); },
            _ => throw new ArgumentException(how),
        };
        await using var srv = new Hostile(behave);
        await using var mon = Mon(srv.Port, tls);
        mon.Start();
        Assert.True(await Until(() => srv.Connections >= 3, 40000), $"stopped after {srv.Connections} connection(s); status {mon.Status}");
    }

    [Fact]
    public async Task Reconnect_button_retries_at_once_during_a_long_backoff()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        await using var mon = new ServerMonitor(new ServerSettings { Host = "127.0.0.1", Port = port }, null,
            TimeSpan.FromMilliseconds(100), backoff: new Backoff(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));
        mon.Start();
        Assert.True(await Until(() => mon.Status is { State: ConnectionState.Reconnecting, NextRetry: not null }));
        await using var srv = new FakeNutServer(port).Add(FakeUps.ApcBackUps()).Start();
        mon.Kick(reconnect: true);
        Assert.True(await Until(() => mon.Status.State == ConnectionState.Connected, 4000));
    }
}
