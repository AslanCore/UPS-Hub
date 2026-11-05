using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using UpsHub.App.ViewModels;
using UpsHub.Core;
using UpsHub.Core.Model;

namespace UpsHub.App.Services;

/// <summary>Notification-area icon: AslanCore logo with a status dot, tooltip with the live reading, and a menu.</summary>
public sealed class TrayService : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _status;
    private readonly Dictionary<UpsHealth, WindowIcon> _icons = new();
    private UpsHealth? _shown;

    private readonly NativeMenuItem _reconnect;

    public TrayService(Application app, Action open, Action testCountdown, Action quit, Action? reconnect = null)
    {
        _status = new NativeMenuItem("Connecting…") { IsEnabled = false };
        var openItem = new NativeMenuItem("Open " + AppInfo.DisplayName);
        openItem.Click += (_, _) => open();
        var testItem = new NativeMenuItem("Test shutdown…");
        testItem.Click += (_, _) => testCountdown();
        _reconnect = new NativeMenuItem("Reconnect now");
        _reconnect.Click += (_, _) => reconnect?.Invoke();
        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => quit();
        _tray = new TrayIcon
        {
            ToolTipText = AppInfo.DisplayName,
            Icon = IconFor(UpsHealth.Unknown),
            Menu = new NativeMenu { Items = { openItem, _status, _reconnect, new NativeMenuItemSeparator(), testItem, new NativeMenuItemSeparator(), quitItem } },
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => open();
        TrayIcon.SetIcons(app, new TrayIcons { _tray });
    }

    public void Update(UpsHealth health, string text)
    {
        _tray.ToolTipText = text;
        _status.Header = text.Replace(AppInfo.DisplayName + " · ", "");
        if (_shown != health) { _tray.Icon = IconFor(health); _shown = health; }
    }

    /// <summary>
    /// Tray icon for a state: a multi-size .ico (16/20/24/32/48) with the simplified glyph drawn for
    /// small sizes and a status dot, made by tools/icon/make_icon.py, so Windows picks a crisp size.
    /// </summary>
    public WindowIcon IconFor(UpsHealth h)
    {
        if (_icons.TryGetValue(h, out var icon)) return icon;
        using var s = AssetLoader.Open(new Uri($"avares://UpsHub/Assets/Tray/tray-{TrayAsset(h)}.ico"));
        icon = new WindowIcon(s);
        _icons[h] = icon;
        return icon;
    }

    public static string TrayAsset(UpsHealth h) => h switch
    {
        UpsHealth.Online => "up",
        UpsHealth.OnBattery => "primary",
        UpsHealth.LowBattery or UpsHealth.ForcedShutdown => "down",
        UpsHealth.Alarm => "degraded",
        _ => "subtle",
    };

    /// <summary>Saves the tray icon variants (for screenshots / docs).</summary>
    public void SaveIcons(string dir)
    {
        foreach (var h in new[] { UpsHealth.Online, UpsHealth.OnBattery, UpsHealth.LowBattery, UpsHealth.Unknown })
        {
            using var f = File.Create(Path.Combine(dir, $"tray-{h.ToString().ToLowerInvariant()}.png"));
            IconFor(h).Save(f);
        }
    }

    public void Dispose()
    {
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
