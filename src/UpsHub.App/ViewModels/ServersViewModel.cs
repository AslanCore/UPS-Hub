using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpsHub.App.Services;
using UpsHub.Core.Local;
using UpsHub.Core.Monitoring;
using UpsHub.Core.Nut;
using UpsHub.Core.Settings;

namespace UpsHub.App.ViewModels;

public sealed partial class ServerItemViewModel : ViewModelBase
{
    public required ServerSettings Config { get; init; }
    public required ServersViewModel Owner { get; init; }
    public bool IsLocal => Config.IsLocal;
    public string Name => !string.IsNullOrWhiteSpace(Config.DisplayName) ? Config.DisplayName
        : IsLocal ? LocalNames.DisplayName(Config.Local!.Vendor, Config.Local.Model)
        : Config.Host;
    /// <summary>"usb · USB HID · 051d:0002" for a local UPS, host:port for NUT.</summary>
    public string Endpoint => IsLocal ? LocalEndpoint(Config.Local!) : $"{Config.Host}:{Config.Port}";
    public string Kicker => IsLocal ? "THIS PC" : "NUT";
    public string ReconnectLabel => IsLocal ? "Reopen device" : "Reconnect";

    private static string LocalEndpoint(LocalDeviceSettings d)
    {
        var parts = d.DeviceKey.Split(':');
        var usb = d.Tier is LocalTier.Hid or LocalTier.Replay && parts.Length >= 2 && parts[0].Length == 4 ? $"{parts[0]}:{parts[1]}" : null;
        return string.Join("  ·  ", new[] { d.Tier == LocalTier.Megatec ? d.DeviceKey : null, LocalNames.TierLabel(d.Tier), usb }.Where(x => x is not null));
    }
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private IBrush _statusBrush = Palette.Brush(Palette.Subtle);
    [ObservableProperty] private IBrush _statusSoft = Palette.Soft(Palette.Subtle);
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _upsText = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _hasMessage;

    [RelayCommand] private void Remove() => Owner.RemoveServer(this);
    [RelayCommand] private void Reconnect() => Owner.Reconnect(this);
}

public sealed partial class ServersViewModel : ViewModelBase
{
    private readonly UpsHubService _svc;
    public ServersViewModel(UpsHubService svc)
    {
        _svc = svc;
        Discovery = new DiscoveryViewModel(svc, Rebuild);
        Local = new LocalUpsViewModel(svc, Rebuild);
        Rebuild();
    }

    public DiscoveryViewModel Discovery { get; }
    public LocalUpsViewModel Local { get; }

    public ObservableCollection<ServerItemViewModel> Servers { get; } = new();
    public ObservableCollection<string> FoundUps { get; } = new();
    public string[] TlsModes { get; } = { "Off", "Prefer TLS", "Require TLS" };

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private decimal _port = 3493;
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _upsName = "";
    [ObservableProperty] private int _tlsIndex;
    [ObservableProperty] private bool _loginAsSecondary;
    [ObservableProperty] private string _probeResult = "";
    [ObservableProperty] private bool _probeOk;
    [ObservableProperty] private bool _hasProbeResult;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _hasServers;

    private ServerSettings Draft() => new()
    {
        Host = Host.Trim(),
        Port = (int)Port,
        DisplayName = DisplayName.Trim(),
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        Tls = (TlsMode)TlsIndex,
        LoginAsSecondary = LoginAsSecondary,
        UpsNames = string.IsNullOrWhiteSpace(UpsName) ? new() : UpsName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
    };

    [RelayCommand]
    private async Task TestAsync()
    {
        if (string.IsNullOrWhiteSpace(Host)) { ShowProbe(false, "Enter a host name or IP address first."); return; }
        Busy = true;
        FoundUps.Clear();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var (ver, ups) = await UpsHubService.ProbeAsync(Draft(), string.IsNullOrEmpty(Password) ? null : Password, cts.Token);
            foreach (var u in ups) FoundUps.Add(u.Name);
            ShowProbe(true, ups.Count == 0
                ? $"Connected ({ver ?? "upsd"}), but the server lists no UPS."
                : $"Connected to {ShortVer(ver)}. Found {ups.Count} UPS: {string.Join(", ", ups.Select(u => string.IsNullOrWhiteSpace(u.Description) ? u.Name : $"{u.Name} ({u.Description})"))}.");
        }
        catch (NutException ex) { ShowProbe(false, NutErrorParser.Describe(ex.Error)); }
        catch (Exception ex) { ShowProbe(false, ex.Message + Hint()); }
        finally { Busy = false; }
    }

    private static string ShortVer(string? v) => v is null ? "upsd" : v.Split(" - ")[0].Replace("Network UPS Tools ", "NUT ");

    private static string Hint() =>
        " Check that upsd LISTENs on the server's LAN address (not only 127.0.0.1) and that port 3493 is open.";

    private void ShowProbe(bool ok, string text) { ProbeOk = ok; ProbeResult = text; HasProbeResult = true; }

    [RelayCommand] private void PickUps(string name) => UpsName = name;

    [RelayCommand]
    private void Add()
    {
        if (string.IsNullOrWhiteSpace(Host)) { ShowProbe(false, "Enter a host name or IP address first."); return; }
        var s = Draft();
        if (!string.IsNullOrEmpty(Password)) s.ProtectedPassword = _svc.Platform.Secrets.Protect(Password);
        _svc.Settings.Servers.Add(s);
        _svc.Save();
        _svc.RebuildMonitors();
        Host = ""; DisplayName = ""; Username = ""; Password = ""; UpsName = ""; Port = 3493; TlsIndex = 0; LoginAsSecondary = false;
        HasProbeResult = false; FoundUps.Clear();
        Rebuild();
        Discovery.RefreshAdded();
    }

    public void RemoveServer(ServerItemViewModel item)
    {
        _svc.Settings.Servers.Remove(item.Config);
        _svc.Save();
        _svc.RebuildMonitors();
        Rebuild();
        Discovery.RefreshAdded();
        Local.RefreshAdded();
    }

    public void Reconnect(ServerItemViewModel item) =>
        _svc.Monitors.FirstOrDefault(m => m.Config.Id == item.Config.Id)?.Kick(reconnect: true);

    public void Rebuild()
    {
        Servers.Clear();
        foreach (var s in _svc.Settings.Servers) Servers.Add(new ServerItemViewModel { Config = s, Owner = this });
        HasServers = Servers.Count > 0;
        Refresh();
    }

    public void Refresh()
    {
        if (Servers.Count != _svc.Settings.Servers.Count || Servers.Select(s => s.Config).Except(_svc.Settings.Servers).Any())
        {
            Rebuild();
            return;
        }
        foreach (var item in Servers)
        {
            var st = _svc.StatusOf(item.Config.Id);
            var mon = _svc.Monitors.FirstOrDefault(m => m.Config.Id == item.Config.Id);
            var (text, color) = st?.State switch
            {
                ConnectionState.Connected => ("Connected", Palette.Up),
                ConnectionState.Connecting => ("Connecting", Palette.Degraded),
                ConnectionState.Reconnecting => (st.NextRetry is { } nr ? $"Retry in {Math.Max(0, (nr - DateTimeOffset.UtcNow).TotalSeconds):0}s" : "Reconnecting", Palette.Degraded),
                _ => (item.Config.Enabled ? "Stopped" : "Disabled", Palette.Subtle),
            };
            item.StatusText = text;
            item.StatusBrush = Palette.Brush(color);
            item.StatusSoft = Palette.Soft(color);
            if (item.IsLocal)
            {
                var kind = st?.ServerVersion ?? LocalNames.TierLabel(item.Config.Local!.Tier);
                item.Detail = $"Read directly by UPS Hub · {kind} · read-only";
                item.UpsText = $"Publishes {(mon?.KnownUps.FirstOrDefault()?.Name ?? "ups")}@usb to the dashboard, tray and shutdown rules";
                item.Message = st?.Message;
                item.HasMessage = !string.IsNullOrEmpty(st?.Message);
                continue;
            }
            var ver = st?.ServerVersion is { } v ? ShortVer(v) : null;
            item.Detail = string.Join("  ·  ", new[] { ver, st?.Tls == true ? "TLS" : null, item.Config.Username is null ? "no login" : $"user {item.Config.Username}", item.Config.LoginAsSecondary ? "upsmon secondary" : null }.Where(x => x is not null));
            var names = mon?.KnownUps.Select(u => u.Name).ToList() ?? new();
            item.UpsText = item.Config.UpsNames.Count > 0 ? "Monitoring " + string.Join(", ", item.Config.UpsNames)
                : names.Count > 0 ? "Monitoring all: " + string.Join(", ", names) : "Monitoring every UPS on this server";
            item.Message = st?.Message;
            item.HasMessage = !string.IsNullOrEmpty(st?.Message);
        }
    }
}

public sealed partial class LocalDeviceItemViewModel : ViewModelBase
{
    public required LocalDeviceInfo Info { get; init; }
    public required LocalUpsViewModel Owner { get; init; }
    public string Title => LocalNames.DisplayName(Info.Vendor, Info.Model);
    public string Tier => LocalNames.TierLabel(Info.Tier);
    public string Meta => string.Join("  ·  ", new[] { Info.UsbId is { Length: > 0 } u ? "USB " + u : null, Info.Serial is { Length: > 0 } s ? "S/N " + s : null }.Where(x => x is not null));
    public string Detail => Info.Detail;
    public bool IsBestTier => Info.Tier is LocalTier.Hid or LocalTier.Replay or LocalTier.Megatec;
    public string Hint => Info.SameAs is not null ? "Same UPS as the HID entry above; basic readings only" : "";
    public bool HasHint => Hint.Length > 0;
    [ObservableProperty] private bool _added;
    [RelayCommand] private void Add() => Owner.Add(this);
}

/// <summary>Servers page section: UPSes plugged into this PC, read without NUT.</summary>
public sealed partial class LocalUpsViewModel : ViewModelBase
{
    private readonly UpsHubService _svc;
    private readonly Action _onAdded;
    public LocalUpsViewModel(UpsHubService svc, Action onAdded)
    {
        _svc = svc; _onAdded = onAdded;
        Limitation = svc.Platform.LocalUps.Limitation;
    }

    public ObservableCollection<LocalDeviceItemViewModel> Devices { get; } = new();
    public string? Limitation { get; }
    public bool HasLimitation => Limitation is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScanLabel))] private bool _scanning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScanLabel))] private bool _scanned;
    public string ScanLabel => Scanning ? "Scanning…" : Scanned ? "Scan again" : "Find USB UPS";
    [ObservableProperty] private bool _probeSerial;
    [ObservableProperty] private string _summary = "Looks for UPSes connected to this PC by USB (HID Power Device or Windows battery) or serial cable.";
    [ObservableProperty] private bool _hasDevices;
    [ObservableProperty] private bool _showEmpty;

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (Scanning) return;
        Scanning = true;
        try
        {
            var list = await _svc.FindLocalAsync(ProbeSerial);
            Devices.Clear();
            // Best tier first: HID, then serial, then the Windows battery view of the same UPS.
            foreach (var d in list.OrderBy(d => d.Tier switch { LocalTier.Hid or LocalTier.Replay => 0, LocalTier.Megatec => 1, _ => 2 }).ThenBy(d => d.Title))
                Devices.Add(new LocalDeviceItemViewModel { Info = d, Owner = this });
            RefreshAdded();
            HasDevices = Devices.Count > 0;
            ShowEmpty = Devices.Count == 0;
            Summary = Devices.Count switch
            {
                0 => "No UPS found on this PC. Check the USB cable, and that the UPS shows as a battery in Windows (or as a HID UPS in Device Manager).",
                _ => $"Found {Devices.Count(d => d.IsBestTier)} UPS connection{(Devices.Count(d => d.IsBestTier) == 1 ? "" : "s")}"
                     + (Devices.Any(d => !d.IsBestTier) ? $" and {Devices.Count(d => !d.IsBestTier)} Windows battery view{(Devices.Count(d => !d.IsBestTier) == 1 ? "" : "s")}" : "")
                     + ". Add one to monitor it like a NUT server.",
            };
        }
        catch (Exception ex) { Summary = "Scan failed: " + ex.Message; }
        finally { Scanning = false; Scanned = true; }
    }

    public void RefreshAdded()
    {
        foreach (var d in Devices) d.Added = _svc.FindConfigured(d.Info) is not null;
    }

    public void Add(LocalDeviceItemViewModel item)
    {
        if (_svc.FindConfigured(item.Info) is not null) return;
        _svc.AddLocal(item.Info);
        RefreshAdded();
        _onAdded();
    }
}
