using UpsHub.Core.Nut;
using UpsHub.FakeNut;

namespace UpsHub.Tests;

public class NutClientTests
{
    private static NutClient Client(FakeNutServer s, TlsMode tls = TlsMode.Off) =>
        new(new NutConnectOptions { Host = "127.0.0.1", Port = s.Port, Tls = tls, CommandTimeout = TimeSpan.FromSeconds(3) });

    [Fact]
    public async Task Version_list_ups_list_var_get_var()
    {
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps("apc")).Add(new FakeUps("rack", "Rack \"B\"").Set("ups.status", "OL")).Start();
        await using var c = Client(srv);
        await c.ConnectAsync();
        Assert.Contains("upsd 2.8.1", await c.GetServerVersionAsync());
        Assert.Equal("1.3", await c.GetProtocolVersionAsync());

        var ups = await c.ListUpsAsync();
        Assert.Equal(new[] { "apc", "rack" }, ups.Select(u => u.Name));
        Assert.Equal("Rack \"B\"", ups[1].Description);

        var vars = await c.ListVarsAsync("apc");
        Assert.Equal("051d", vars["ups.vendorid"]);
        Assert.Equal("Back-UPS XS 1400U", vars["device.model"]);
        Assert.Equal("100", await c.GetVarAsync("apc", "battery.charge"));
    }

    [Fact]
    public async Task Errors_map_to_codes()
    {
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps("apc")).Start();
        await using var c = Client(srv);
        await c.ConnectAsync();
        var e1 = await Assert.ThrowsAsync<NutException>(() => c.ListVarsAsync("nope"));
        Assert.Equal(NutError.UnknownUps, e1.Error);
        var e2 = await Assert.ThrowsAsync<NutException>(() => c.GetVarAsync("apc", "no.such.var"));
        Assert.Equal(NutError.VarNotSupported, e2.Error);
        srv.Ups["apc"].Stale = true;
        var e3 = await Assert.ThrowsAsync<NutException>(() => c.ListVarsAsync("apc"));
        Assert.Equal(NutError.DataStale, e3.Error);
        // connection still usable after errors
        srv.Ups["apc"].Stale = false;
        Assert.NotEmpty(await c.ListVarsAsync("apc"));
    }

    [Fact]
    public async Task Auth_and_login()
    {
        await using var srv = new FakeNutServer { RequiredUsername = "monuser", RequiredPassword = "s3cret" }.Add(FakeUps.ApcBackUps("apc")).Start();
        await using (var anon = Client(srv))
        {
            await anon.ConnectAsync();
            var e = await Assert.ThrowsAsync<NutException>(() => anon.ListUpsAsync());
            Assert.Equal(NutError.AccessDenied, e.Error);
        }
        await using var c = Client(srv);
        await c.ConnectAsync();
        await c.AuthenticateAsync("monuser", "s3cret");
        await c.LoginAsync("apc");
        Assert.Single(await c.ListUpsAsync());
        var bad = await Assert.ThrowsAsync<NutException>(() => c.LoginAsync("ghost"));
        Assert.Equal(NutError.UnknownUps, bad.Error);
        Assert.Contains("PASSWORD ***", srv.Received);
        Assert.DoesNotContain(srv.Received, l => l.Contains("s3cret"));
    }

    [Fact]
    public async Task Password_is_redacted_in_exceptions()
    {
        await using var srv = new FakeNutServer().Start();
        await using var c = Client(srv);
        await c.ConnectAsync();
        await c.AuthenticateAsync(null, "first");
        var e = await Assert.ThrowsAsync<NutException>(() => c.AuthenticateAsync(null, "hunter2"));
        Assert.DoesNotContain("hunter2", e.Message);
    }

    [Fact]
    public async Task StartTls_prefer_falls_back_require_fails()
    {
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps()).Start();
        await using (var prefer = Client(srv, TlsMode.Prefer))
        {
            await prefer.ConnectAsync();
            Assert.False(prefer.IsTls);
            Assert.Single(await prefer.ListUpsAsync());
        }
        await using var require = Client(srv, TlsMode.Require);
        var e = await Assert.ThrowsAsync<NutException>(() => require.ConnectAsync());
        Assert.Equal(NutError.FeatureNotConfigured, e.Error);
    }

    [Fact]
    public async Task Refused_connection_is_a_connection_exception()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        await using var c = new NutClient(new NutConnectOptions { Host = "127.0.0.1", Port = port });
        await Assert.ThrowsAsync<NutConnectionException>(() => c.ConnectAsync());
    }

    [Fact]
    public async Task Dropped_connection_is_detected()
    {
        await using var srv = new FakeNutServer().Add(FakeUps.ApcBackUps()).Start();
        await using var c = Client(srv);
        await c.ConnectAsync();
        await c.ListUpsAsync();
        srv.DropAllClients();
        await Task.Delay(100);
        await Assert.ThrowsAsync<NutConnectionException>(() => c.ListUpsAsync());
    }
}
