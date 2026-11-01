using System.Text.RegularExpressions;
using UpsHub.App.Platform;
using UpsHub.App.Services;
using UpsHub.App.ViewModels;
using UpsHub.Core.Model;
using UpsHub.Core.Settings;

namespace UpsHub.Tests;

/// <summary>
/// Guards against the 0.1–0.3 idle GPU load: an infinite opacity animation on the status dots kept the
/// compositor (and DWM) redrawing every frame, and every poll re-set brushes/arrays to new instances.
/// </summary>
public class RenderingTests
{
    internal static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "UpsHub.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void No_xaml_or_code_declares_a_looping_animation()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var bad = new List<string>();
        foreach (var f in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".axaml") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(f);
            if (Regex.IsMatch(text, @"IterationCount\s*=\s*""?\s*(INFINITE|Infinite)", RegexOptions.IgnoreCase)
                || text.Contains("IterationCount.Infinite") || text.Contains("PlaybackDirection=\"Alternate"))
                bad.Add(Path.GetRelativePath(src, f));
            if (Regex.IsMatch(text, @"TransparencyLevelHint\s*=\s*""(Mica|AcrylicBlur|Blur)"))
                bad.Add(Path.GetRelativePath(src, f) + " (blur/acrylic)");
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void Palette_returns_the_same_brush_for_the_same_colour()
    {
        Assert.Same(Palette.Brush(Palette.Up), Palette.Brush(Palette.Up));
        Assert.Same(Palette.Soft(Palette.Down), Palette.Soft(Palette.Down));
        Assert.NotSame(Palette.Brush(Palette.Up), Palette.Brush(Palette.Down));
    }

    [Theory]
    [InlineData(1, "updated just now")]
    [InlineData(9, "updated just now")]
    [InlineData(30, "updated under a minute ago")]
    [InlineData(150, "updated 2 min ago")]
    public void Age_text_does_not_tick_every_second(int seconds, string text) =>
        Assert.Equal(text, DashboardViewModel.AgeText(false, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Unchanged_readings_raise_no_property_changes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "upshub-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new UpsHubService(new SettingsStore(dir), PlatformServices.Create(dryRunPower: true, iconPath: null));
            var main = new MainViewModel(svc);
            var a = new UpsAddress("ups", "10.0.0.2", 3493);
            UpsSnapshot Snap() => UpsSnapshot.FromVariables("ups", new Dictionary<string, string>
            {
                ["ups.status"] = "OL", ["battery.charge"] = "100", ["battery.runtime"] = "2400", ["ups.load"] = "12",
                ["input.voltage"] = "231.0", ["output.voltage"] = "230.0", ["device.mfr"] = "APC", ["device.model"] = "Back-UPS",
            }, DateTimeOffset.UtcNow);

            // Fill the sparkline history so it no longer grows.
            for (int i = 0; i < 100; i++) { svc.OnSnapshot(a, Snap()); main.Refresh(); }

            var changed = new List<string>();
            main.Dashboard.PropertyChanged += (_, e) => changed.Add("Dashboard." + e.PropertyName);
            main.PropertyChanged += (_, e) => changed.Add("Main." + e.PropertyName);
            foreach (var u in main.SidebarUps) u.PropertyChanged += (_, e) => changed.Add("Sidebar." + e.PropertyName);
            foreach (var c in main.Dashboard.Chips) c.PropertyChanged += (_, e) => changed.Add("Chip." + e.PropertyName);

            for (int i = 0; i < 5; i++) { svc.OnSnapshot(a, Snap()); main.Refresh(); main.Dashboard.Tick(); }
            Assert.Empty(changed);

            // A real change is still reported.
            svc.OnSnapshot(a, UpsSnapshot.FromVariables("ups", new Dictionary<string, string> { ["ups.status"] = "OB DISCHRG", ["battery.charge"] = "97" }, DateTimeOffset.UtcNow));
            main.Refresh();
            Assert.Contains("Dashboard.BatteryText", changed);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Visual_clock_stops_while_hidden_and_resumes_on_show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "upshub-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            var main = new MainViewModel(new UpsHubService(new SettingsStore(dir), PlatformServices.Create(true, null)));
            Assert.True(main.ClockRunning);
            main.SetWindowActive(false);
            Assert.False(main.ClockRunning);
            main.SetWindowActive(true);
            Assert.True(main.ClockRunning);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
