using UpsHub.App.Platform;
using UpsHub.App.Services;
using UpsHub.Core.Monitoring;
using UpsHub.Core.Nut;
using UpsHub.Core.Settings;
using UpsHub.FakeNut;

namespace UpsHub.Tests;

public class ServerEditTests
{
    private static ServerSettings Srv() => new() { Host = "nut.example", Port = 3493, Username = "mon", ProtectedPassword = "enc:old", DisplayName = "NAS" };

    [Theory]
    [InlineData("", 3493, "Host")]
    [InlineData("bad host", 3493, "Host")]
    [InlineData("nut.example", 0, "Port")]
    [InlineData("nut.example", 70000, "Port")]
    public void Validation_rejects_bad_fields(string host, int port, string field)
    {
        var e = ServerEdit.From(Srv(), new[] { "apc" }, null) with { Host = host, Port = port };
        Assert.True(e.Validate().ContainsKey(field));
    }

    [Fact]
    public void Valid_edit_passes_and_ipv6_is_accepted()
    {
        Assert.Empty((ServerEdit.From(Srv(), new[] { "apc" }, null)).Validate());
        Assert.Empty((ServerEdit.From(Srv(), new[] { "apc" }, null) with { Host = "2001:db8::5" }).Validate());
        var e = ServerEdit.From(Srv(), new[] { "apc" }, null) with { PollIntervalSeconds = 0 };
        Assert.True(e.Validate().ContainsKey("PollInterval"));
    }

    [Fact]
    public void Password_left_empty_is_unchanged_and_clear_removes_it()
    {
        var s = Srv(); var all = new AppSettings();
        var e = ServerEdit.From(s, new[] { "apc" }, null);
        Assert.Null(e.NewPassword);
        Assert.False(e.NeedsReconnect(s));
        e.ApplyTo(s, all, p => "enc:" + p);
        Assert.Equal("enc:old", s.ProtectedPassword);
        (e with { NewPassword = "n3w" }).ApplyTo(s, all, p => "enc:" + p);
        Assert.Equal("enc:n3w", s.ProtectedPassword);
        (e with { NewPassword = "" }).ApplyTo(s, all, p => "enc:" + p);
        Assert.Null(s.ProtectedPassword);
    }

    [Fact]
    public void Only_connection_changes_need_a_reconnect()
    {
        var s = Srv();
        var e = ServerEdit.From(s, new[] { "apc" }, null);
        Assert.False((e with { DisplayName = "Rack", PollIntervalSeconds = 10 }).NeedsReconnect(s));
        Assert.True((e with { Host = "nut2.example" }).NeedsReconnect(s));
        Assert.True((e with { Port = 3494 }).NeedsReconnect(s));
        Assert.True((e with { Tls = TlsMode.Require }).NeedsReconnect(s));
        Assert.True((e with { Username = "other" }).NeedsReconnect(s));
        Assert.True((e with { NewPassword = "x" }).NeedsReconnect(s));
    }

    [Fact]
    public void Per_ups_prefs_and_primary_follow_the_edit()
    {
        var s = Srv(); var all = new AppSettings();
        all.Shutdown.PrimaryUps = "apc@nut.example";
        var e = ServerEdit.From(s, new[] { "apc", "rack" }, all.Shutdown.PrimaryUps);
        Assert.True(e.Ups.Single(u => u.Name == "apc").Primary);
        e = e with
        {
            Host = "nut2.example",
            Ups = new[] { new UpsEdit("apc", "Desk UPS", true, false, false), new UpsEdit("rack", "", false, true, true) },
        };
        Assert.True(e.NeedsReconnect(s));
        e.ApplyTo(s, all, p => p);
        Assert.Equal("rack@nut2.example", all.Shutdown.PrimaryUps);
        Assert.Equal(new[] { "apc" }, s.UpsNames);
        Assert.False(s.IsMonitored("rack"));
        Assert.Equal("Desk UPS", s.PrefsFor("apc")!.DisplayName);
        Assert.False(s.PrefsFor("apc")!.Notifications);
    }

    [Fact]
    public void Two_primaries_or_nothing_monitored_is_invalid()
    {
        var e = ServerEdit.From(Srv(), new[] { "a", "b" }, null);
        Assert.True((e with { Ups = new[] { new UpsEdit("a", "", true, true, true), new UpsEdit("b", "", true, true, true) } }).Validate().ContainsKey("Primary"));
        Assert.True((e with { Ups = new[] { new UpsEdit("a", "", false, true, false) } }).Validate().ContainsKey("Monitored"));
    }

    [Fact]
    public void Local_usb_edit_keeps_device_and_renames()
    {
        var s = new ServerSettings { Kind = UpsSourceKind.Local, Host = SettingsStore.LocalHost, Local = new LocalDeviceSettings { DeviceKey = "051d:0002:X" } };
        var all = new AppSettings();
        var e = ServerEdit.From(s, new[] { "ups" }, null) with { DisplayName = "Desk", PollIntervalSeconds = 5,
            Ups = new[] { new UpsEdit("ups", "", true, true, true) } };
        Assert.Empty(e.Validate());
        e.ApplyTo(s, all, p => p);
        Assert.Equal("Desk", s.DisplayName);
        Assert.Equal(5, s.PollIntervalSeconds);
        Assert.Equal("usb", s.Host);
        Assert.Equal("ups@usb", all.Shutdown.PrimaryUps);
    }

    private static async Task<bool> Until(Func<bool> cond, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    [Fact]
    public async Task Service_applies_edit_live_and_reconnects_to_the_new_port()
    {
        await using var a = new FakeNutServer().Add(FakeUps.ApcBackUps()).Start();
        await using var b = new FakeNutServer().Add(FakeUps.ApcBackUps()).Start();
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var svc = new UpsHubService(new SettingsStore(dir), PlatformServices.Create(true, null));
            var cfg = new ServerSettings { Host = "127.0.0.1", Port = a.Port };
            svc.Settings.Servers.Add(cfg);
            svc.RebuildMonitors();
            Assert.True(await Until(() => svc.StatusOf(cfg.Id)?.State == ConnectionState.Connected));
            var first = svc.Monitors.Single();

            // Rename only: same monitor, no reconnect.
            Assert.Empty(svc.ApplyServerEdit(ServerEdit.From(cfg, new[] { "apc" }, null) with { DisplayName = "Lab", PollIntervalSeconds = 7 }));
            Assert.Same(first, svc.Monitors.Single());
            Assert.Equal(TimeSpan.FromSeconds(7), first.PollInterval);
            Assert.Equal(1, a.ConnectionCount);

            // Port change: monitor replaced, connects to the new server.
            Assert.Empty(svc.ApplyServerEdit(ServerEdit.From(cfg, new[] { "apc" }, null) with { Port = b.Port }));
            Assert.NotSame(first, svc.Monitors.Single());
            Assert.True(await Until(() => b.ConnectionCount >= 1 && svc.Monitors.Single().Status.State == ConnectionState.Connected));

            // Invalid edit is refused and nothing changes.
            Assert.NotEmpty(svc.ApplyServerEdit(ServerEdit.From(cfg, new[] { "apc" }, null) with { Port = 0 }));
            Assert.Equal(b.Port, cfg.Port);

            // Reconnect button path: a stopped monitor starts again.
            await svc.Monitors.Single().StopAsync();
            Assert.True(svc.Reconnect(cfg.Id));
            Assert.True(await Until(() => svc.Monitors.Single().Status.State == ConnectionState.Connected));

            svc.RemoveServer(cfg.Id);
            Assert.Empty(svc.Monitors);
            Assert.Empty(svc.Settings.Servers);
            await svc.DisposeAsync();
        }
        finally { Directory.Delete(dir, true); }
    }
}
