using UpsHub.Core;
using UpsHub.Core.Platform;
using UpsHub.Core.Settings;

namespace UpsHub.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "upshub-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Round_trip_and_enums_as_strings()
    {
        var store = new SettingsStore(_dir);
        var s = new AppSettings();
        s.Servers.Add(new ServerSettings { Host = "nut.lan", DisplayName = "Proxmox", UpsNames = { "apc" }, Tls = Core.Nut.TlsMode.Prefer });
        s.Shutdown.Action = ShutdownAction.Hibernate;
        store.Save(s);
        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"hibernate\"", json, StringComparison.OrdinalIgnoreCase);
        var back = store.Load();
        Assert.Equal("nut.lan", back.Servers[0].Host);
        Assert.Equal(ShutdownAction.Hibernate, back.Shutdown.Action);
        Assert.Equal(Core.Nut.TlsMode.Prefer, back.Servers[0].Tls);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_backup_then_defaults()
    {
        var store = new SettingsStore(_dir);
        var s = new AppSettings(); s.General.PollIntervalSeconds = 7;
        store.Save(s);
        store.Save(s);                     // creates .bak
        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Equal(7, store.Load().General.PollIntervalSeconds);
        File.WriteAllText(store.FilePath + ".bak", "garbage");
        Assert.Equal(2, store.Load().General.PollIntervalSeconds);
    }

    [Fact]
    public void Values_are_clamped()
    {
        var store = new SettingsStore(_dir);
        var s = new AppSettings();
        s.General.PollIntervalSeconds = 0; s.Shutdown.BatteryPercentBelow = 400;
        s.Servers.Add(new ServerSettings { Host = "x", Port = 0 });
        store.Save(s);
        var b = store.Load();
        Assert.Equal(1, b.General.PollIntervalSeconds);
        Assert.Equal(100, b.Shutdown.BatteryPercentBelow);
        Assert.Equal(3493, b.Servers[0].Port);
    }

    [Fact]
    public void Plain_protector_round_trips_and_rejects_foreign_values()
    {
        var p = new PlainSecretProtector();
        Assert.Equal("pw", p.Unprotect(p.Protect("pw")));
        Assert.Null(p.Unprotect("dpapi:AAAA"));
        Assert.Null(p.Unprotect(null));
    }

    [Fact]
    public void AppInfo_reads_branding_from_build_props()
    {
        Assert.Equal("UPS Hub", AppInfo.DisplayName);
        Assert.Equal("AslanCore.UpsHub", AppInfo.AppId);
        Assert.Equal("UPS Hub", AppInfo.SettingsFolderName);
    }
}
