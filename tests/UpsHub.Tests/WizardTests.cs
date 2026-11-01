using UpsHub.App.Platform;
using UpsHub.App.Services;
using UpsHub.App.ViewModels;
using UpsHub.Core;
using UpsHub.Core.Platform;
using UpsHub.Core.Settings;

namespace UpsHub.Tests;

public class WizardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "upshub-wizard-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private UpsHubService Svc() => new(new SettingsStore(_dir), PlatformServices.Create(dryRunPower: true, iconPath: null));

    private sealed class CountingImporter : ILegacySettingsImporter
    {
        public int Calls;
        private readonly DemoWinNutImporter _inner = new();
        public string? TryImport(AppSettings target) { Calls++; return _inner.TryImport(target); }
    }

    [Fact]
    public void Fresh_install_needs_setup_and_existing_configs_do_not()
    {
        var svc = Svc();
        Assert.True(svc.IsFirstRun);
        Assert.True(svc.NeedsSetup);
        svc.Settings.Servers.Add(new ServerSettings { Host = "10.0.0.2" });
        Assert.False(svc.NeedsSetup);
    }

    [Fact]
    public void Steps_walk_forward_and_back()
    {
        var closed = false;
        var wz = new SetupWizardViewModel(Svc(), () => closed = true, new NullLegacyImporter());
        Assert.True(wz.IsWelcome);
        Assert.False(wz.ShowNav);
        Assert.False(wz.HasWinNut);
        wz.Mode = ConnectMode.Manual;            // no scan started for manual
        wz.BeginCommand.Execute(null);
        Assert.True(wz.IsConnect);
        Assert.Equal("Skip for now", wz.NextText);
        wz.NextCommand.Execute(null);
        Assert.True(wz.IsShutdownStep);
        wz.BackCommand.Execute(null);
        Assert.True(wz.IsConnect);
        wz.NextCommand.Execute(null); wz.NextCommand.Execute(null);
        Assert.True(wz.IsStartup);
        Assert.Equal("Finish setup", wz.NextText);
        Assert.False(closed);
    }

    [Fact]
    public void Import_preview_changes_nothing_until_applied_and_asks_for_the_password()
    {
        var svc = Svc();
        var imp = new CountingImporter();
        var wz = new SetupWizardViewModel(svc, () => { }, imp);
        Assert.True(wz.HasWinNut);
        Assert.True(wz.ImportHasServer);
        Assert.Contains(wz.ImportPreview, l => l.Contains("127.0.0.1:3493"));
        Assert.Contains(wz.ImportPreview, l => l.Contains("hibernate below 35%"));
        Assert.Empty(svc.Settings.Servers);                       // preview went to a scratch copy
        Assert.Equal(ShutdownAction.Shutdown, svc.Settings.Shutdown.Action);

        wz.ImportUsername = "upsmon";
        wz.ImportPassword = "secret";
        wz.ApplyImportCommand.Execute(null);
        var s = Assert.Single(svc.Settings.Servers);
        Assert.Equal("upsmon", s.Username);
        Assert.NotNull(s.ProtectedPassword);
        Assert.Equal("", wz.ImportPassword);                     // not kept in memory
        Assert.Equal(1, wz.ConnectedCount);
        Assert.Equal("Continue", wz.NextText);
        wz.ApplyImportCommand.Execute(null);                      // only once
        Assert.Single(svc.Settings.Servers);

        // The shutdown step picks up the imported rules.
        wz.Step = SetupStep.Shutdown;
        Assert.Equal(ShutdownAction.Hibernate, wz.Action);
        Assert.Equal(35, wz.BatteryBelow);
        Assert.Equal(5, wz.RuntimeMinutesBelow);
    }

    [Fact]
    public void Manual_add_validates_and_deduplicates()
    {
        var svc = Svc();
        var wz = new SetupWizardViewModel(svc, () => { }, new NullLegacyImporter());
        wz.AddManualCommand.Execute(null);
        Assert.False(wz.ManualOk);
        Assert.Empty(svc.Settings.Servers);
        wz.Host = "192.0.2.20"; wz.UpsName = "apc"; wz.Password = "pw";
        wz.AddManualCommand.Execute(null);
        var s = Assert.Single(svc.Settings.Servers);
        Assert.Equal("192.0.2.20", s.Host);
        Assert.Equal(new[] { "apc" }, s.UpsNames);
        Assert.NotNull(s.ProtectedPassword);
        wz.Host = "192.0.2.20";
        wz.AddManualCommand.Execute(null);
        Assert.Single(svc.Settings.Servers);
        Assert.Contains("already added", wz.ManualResult);
    }

    [Fact]
    public void Finish_writes_rules_and_general_settings_and_marks_setup_done()
    {
        var svc = Svc();
        var wz = new SetupWizardViewModel(svc, () => { }, new NullLegacyImporter());
        wz.Step = SetupStep.Shutdown;
        wz.SetActionCommand.Execute("Sleep");
        wz.BatteryBelow = 40; wz.RuntimeMinutesBelow = 3; wz.CountdownSeconds = 20;
        Assert.Contains("sleep this PC when the charge drops below 40%", wz.RuleSummary);
        wz.Step = SetupStep.Startup;
        wz.StartMinimized = true; wz.Notifications = false;
        wz.NextCommand.Execute(null);
        Assert.True(wz.IsDone);
        var r = svc.Settings.Shutdown;
        Assert.Equal(ShutdownAction.Sleep, r.Action);
        Assert.Equal(40, r.BatteryPercentBelow);
        Assert.Equal(180, r.RuntimeSecondsBelow);
        Assert.Equal(20, r.CountdownSeconds);
        Assert.True(svc.Settings.General.StartMinimized);
        Assert.False(svc.Settings.General.Notifications);
        Assert.False(svc.Settings.General.StartWithWindows);       // not supported off Windows
        Assert.True(svc.Settings.SetupCompleted);
        Assert.False(svc.NeedsSetup);
        Assert.True(new SettingsStore(_dir).Load().SetupCompleted); // saved
        Assert.Contains("No UPS connected yet", wz.DoneSummary);
    }

    [Fact]
    public void Later_closes_without_asking_again()
    {
        var svc = Svc();
        var closed = false;
        var wz = new SetupWizardViewModel(svc, () => closed = true, new NullLegacyImporter());
        wz.LaterCommand.Execute(null);
        Assert.True(closed);
        Assert.False(svc.NeedsSetup);
    }

    [Fact]
    public void Reset_rules_gives_the_documented_defaults()
    {
        var wz = new SetupWizardViewModel(Svc(), () => { }, new NullLegacyImporter());
        wz.BatteryBelow = 80; wz.SetActionCommand.Execute("Hibernate");
        wz.ResetRulesCommand.Execute(null);
        var d = new ShutdownRules();
        Assert.Equal(d.BatteryPercentBelow, wz.BatteryBelow);
        Assert.Equal(d.Action, wz.Action);
        Assert.Equal(d.CountdownSeconds, wz.CountdownSeconds);
    }
}

public class VersionTests
{
    [Fact]
    public void About_version_is_the_build_version_from_Directory_Build_props()
    {
        var props = File.ReadAllText(Path.Combine(RenderingTests.RepoRoot(), "Directory.Build.props"));
        var m = System.Text.RegularExpressions.Regex.Match(props, @"<Version>([^<]+)</Version>");
        Assert.True(m.Success);
        var built = m.Groups[1].Value;
        Assert.Equal(built, AppInfo.Version);
        // The exe's own informational version agrees with the one the About card shows.
        var appInfo = typeof(UpsHub.App.App).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
        Assert.Equal(built, appInfo);
        var dir = Path.Combine(Path.GetTempPath(), "upshub-ver-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new SettingsViewModel(new UpsHubService(new SettingsStore(dir), PlatformServices.Create(true, null)));
            Assert.Equal("v" + built, s.Version);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Update_check_compares_against_the_build_version() =>
        Assert.Equal(UpsHub.Core.Update.SemVer.TryParse(AppInfo.Version), UpdateService.Current);
}
