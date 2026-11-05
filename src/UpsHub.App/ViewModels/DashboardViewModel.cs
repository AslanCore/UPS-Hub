using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpsHub.App.Services;
using UpsHub.Core.Model;
using UpsHub.Core.Monitoring;
using UpsHub.Core.Shutdown;

namespace UpsHub.App.ViewModels;

public sealed partial class UpsChipViewModel : ViewModelBase
{
    public required UpsAddress Address { get; init; }
    public string Label => Address.Ups;
    public string Host => Address.Port == 3493 ? Address.Host : $"{Address.Host}:{Address.Port}";
    [ObservableProperty] private IBrush _dot = Palette.Brush(Palette.Subtle);
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _summary = "";
}

public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly UpsHubService _svc;
    private readonly MainViewModel _main;
    private readonly Dictionary<UpsAddress, (List<double> Charge, List<double> Load)> _history = new();
    private const int HistoryLength = 90;
    private UpsAddress? _selected;

    public DashboardViewModel(UpsHubService svc, MainViewModel main)
    {
        _svc = svc;
        _main = main;
        _svc.SnapshotArrived += (a, s) => Record(a, s);
    }

    public ObservableCollection<UpsChipViewModel> Chips { get; } = new();

    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _showEmpty = true;
    [ObservableProperty] private bool _showWaiting;
    [ObservableProperty] private string _kicker = "UPS HUB";
    [ObservableProperty] private string _title = "No UPS yet.";
    [ObservableProperty] private string _subtitle = "Add your NUT server to start monitoring.";
    [ObservableProperty] private string _pillText = "Offline";
    [ObservableProperty] private IBrush _pillBrush = Palette.Brush(Palette.Subtle);
    [ObservableProperty] private IBrush _pillSoft = Palette.Soft(Palette.Subtle);
    [ObservableProperty] private bool _pillPulse;
    [ObservableProperty] private double _battery;
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private string _batteryText = "–";
    [ObservableProperty] private IBrush _batteryBrush = Palette.Brush(Palette.Meter);
    [ObservableProperty] private string _batteryState = "";
    [ObservableProperty] private string _runtimeText = "–";
    [ObservableProperty] private string _runtimeUnit = "";
    [ObservableProperty] private string _runtimeNote = "";
    [ObservableProperty] private double _load;
    [ObservableProperty] private bool _hasLoad;
    [ObservableProperty] private string _loadText = "–";
    [ObservableProperty] private IBrush _loadBrush = Palette.Brush(Palette.Primary);
    [ObservableProperty] private string _wattsText = "–";
    [ObservableProperty] private string _wattsNote = "";
    [ObservableProperty] private string _inputVText = "–";
    [ObservableProperty] private double _inputV;
    [ObservableProperty] private bool _hasInputV;
    [ObservableProperty] private string _outputVText = "–";
    [ObservableProperty] private double _outputV;
    [ObservableProperty] private bool _hasOutputV;
    [ObservableProperty] private string _batteryVText = "–";
    [ObservableProperty] private string _freqText = "–";
    [ObservableProperty] private string _updatedText = "";
    [ObservableProperty] private string _flagsText = "";
    [ObservableProperty] private string _deviceText = "";
    [ObservableProperty] private string _ruleText = "";
    [ObservableProperty] private IReadOnlyList<double>? _chargeHistory;
    [ObservableProperty] private UpdateViewModel? _updates;
    [ObservableProperty] private IReadOnlyList<double>? _loadHistory;
    [ObservableProperty] private bool _multipleUps;

    [RelayCommand] private void Select(UpsChipViewModel chip) { _selected = chip.Address; Refresh(); }
    [RelayCommand] private void AddServer() => _main.Navigate("servers");
    /// <summary>Hero "Reconnect now": the source behind the shown UPS, or every source that is down.</summary>
    [RelayCommand]
    private void Reconnect()
    {
        var addr = _selected ?? _svc.PrimaryAddress;
        if (addr is { } a && _svc.MonitorOf(a) is { } m) _svc.Reconnect(m.Config.Id);
        else _svc.ReconnectAll();
        Refresh();
    }
    [ObservableProperty] private bool _showReconnect;

    [ObservableProperty] private bool _showFirstRun;

    [RelayCommand]
    private void FindServers()
    {
        DismissFirstRun();
        _main.Navigate("servers");
        _ = _main.Servers.Discovery.StartAsync();
    }

    [RelayCommand] private void NotNow() { DismissFirstRun(); Refresh(); }

    private void DismissFirstRun()
    {
        if (_svc.Settings.Discovery.FirstRunPromptDone) return;
        _svc.Settings.Discovery.FirstRunPromptDone = true;
        _svc.Save();
    }

    private void Record(UpsAddress a, UpsSnapshot s)
    {
        if (s.Stale) return;
        if (!_history.TryGetValue(a, out var h)) _history[a] = h = (new List<double>(), new List<double>());
        if (s.BatteryCharge is { } c) { h.Charge.Add(c); if (h.Charge.Count > HistoryLength) h.Charge.RemoveAt(0); }
        if (s.Load is { } l) { h.Load.Add(l); if (h.Load.Count > HistoryLength) h.Load.RemoveAt(0); }
    }

    private static string F(double? v, string fmt = "0") => v is { } d ? d.ToString(fmt, CultureInfo.InvariantCulture) : "–";

    public void Refresh()
    {
        var snaps = _svc.Snapshots;
        // chips
        var keys = snaps.Keys.OrderBy(k => k.ToString()).ToList();
        if (Chips.Count != keys.Count || Chips.Select(c => c.Address).Except(keys).Any())
        {
            Chips.Clear();
            foreach (var k in keys) Chips.Add(new UpsChipViewModel { Address = k });
        }
        if (_selected is { } sel && !snaps.ContainsKey(sel)) _selected = null;
        var addr = _selected ?? _svc.PrimaryAddress;
        foreach (var c in Chips)
        {
            var s = snaps[c.Address];
            c.Dot = Palette.Brush(Palette.For(s.Health));
            c.IsSelected = addr is { } a && c.Address == a;
            c.Summary = s.BatteryCharge is { } ch ? $"{ch:0}%" : "–";
        }
        MultipleUps = Chips.Count > 1;

        var anyServer = _svc.Settings.Servers.Count > 0;
        ShowFirstRun = false;   // first run is the setup wizard now
        ShowEmpty = !anyServer;
        UpsSnapshot? snap = addr is { } ad ? snaps.GetValueOrDefault(ad) : null;
        HasData = snap is not null;
        ShowWaiting = anyServer && snap is null;

        if (snap is null || addr is null)
        {
            Kicker = "UPS HUB";
            Title = anyServer ? "Connecting." : "No UPS yet.";
            Subtitle = anyServer ? StatusLine() : "Add your NUT server or a USB UPS on this PC to start monitoring.";
            SetPill("Offline", Palette.Subtle, false);
            ShowReconnect = anyServer && _svc.Monitors.Any(m => m.Status.State != ConnectionState.Connected);
            return;
        }

        var connected = _svc.IsConnected(addr.Value);
        ShowReconnect = !connected;
        var health = snap.Health;
        var source = _svc.SourceLabelOf(addr.Value);   // "USB · APC Back-UPS BK650M2-CH" for a UPS on this PC
        Kicker = source is null ? addr.Value.ToString().ToUpperInvariant() : $"THIS PC · {addr.Value.Ups.ToUpperInvariant()}@USB";
        Title = !connected ? "Lost contact." : health switch
        {
            UpsHealth.Online => "On line.",
            UpsHealth.OnBattery => "On battery.",
            UpsHealth.LowBattery => "Low battery.",
            UpsHealth.ForcedShutdown => "Shutting down.",
            UpsHealth.Alarm => "Needs attention.",
            _ => snap.Stale ? "Data is stale." : "Unknown.",
        };
        var model = string.Join(" ", new[] { snap.Manufacturer, snap.Model }.Where(x => !string.IsNullOrWhiteSpace(x)));
        Subtitle = !connected ? StatusLine() :
            (source ?? (string.IsNullOrWhiteSpace(model) ? "UPS" : model)) + (snap.Stale ? " · readings are stale" : "");
        var pill = !connected ? ("Reconnecting", Palette.Degraded) : health switch
        {
            UpsHealth.Online => ("Online", Palette.Up),
            UpsHealth.OnBattery => ("On battery", Palette.Primary),
            UpsHealth.LowBattery => ("Low battery", Palette.Down),
            UpsHealth.ForcedShutdown => ("FSD", Palette.Down),
            UpsHealth.Alarm => ("Alarm", Palette.Degraded),
            _ => ("Stale", Palette.Subtle),
        };
        SetPill(pill.Item1, pill.Item2, connected && !snap.Stale);

        HasBattery = snap.BatteryCharge.HasValue;
        Battery = snap.BatteryCharge ?? 0;
        BatteryText = F(snap.BatteryCharge);
        BatteryBrush = Palette.Brush(Palette.ForCharge(snap.BatteryCharge ?? 100));
        BatteryState = snap.Status.HasFlag(UpsStatus.CHRG) ? "CHARGING" :
                       snap.Status.HasFlag(UpsStatus.DISCHRG) || snap.Status.HasFlag(UpsStatus.OB) ? "DISCHARGING" :
                       snap.BatteryCharge >= 99 ? "FULL" : "IDLE";

        if (snap.BatteryRuntimeSeconds is { } rt)
        {
            var t = TimeSpan.FromSeconds(rt);
            if (t.TotalHours >= 1) { RuntimeText = $"{(int)t.TotalHours}:{t.Minutes:00}"; RuntimeUnit = "h"; }
            else { RuntimeText = $"{(int)t.TotalMinutes}:{t.Seconds:00}"; RuntimeUnit = "min"; }
        }
        else { RuntimeText = "–"; RuntimeUnit = ""; }
        RuntimeNote = snap.Status.HasFlag(UpsStatus.OB) ? "remaining on battery" : "estimated at current load";

        HasLoad = snap.Load.HasValue;
        Load = snap.Load ?? 0;
        LoadText = F(snap.Load);
        LoadBrush = Palette.Brush(Palette.ForLoad(snap.Load ?? 0));
        WattsText = F(snap.RealPowerWatts);
        WattsNote = snap.RealPowerWatts is null ? "not reported" : snap.PowerIsEstimated ? "estimated from load" : "measured";

        HasInputV = snap.InputVoltage.HasValue; InputV = snap.InputVoltage ?? 0; InputVText = F(snap.InputVoltage, "0.#");
        HasOutputV = snap.OutputVoltage.HasValue; OutputV = snap.OutputVoltage ?? 0; OutputVText = F(snap.OutputVoltage, "0.#");
        BatteryVText = F(snap.BatteryVoltage, "0.0");
        FreqText = F(snap.OutputFrequency ?? snap.InputFrequency, "0.#");
        FlagsText = string.IsNullOrWhiteSpace(snap.RawStatus) ? "–" : snap.RawStatus;
        DeviceText = string.Join("  ·  ", new[]
        {
            snap.DriverName is null ? null : $"driver {snap.DriverName}",
            snap.Variables.TryGetValue("ups.vendorid", out var vid) && snap.Variables.TryGetValue("ups.productid", out var pid) ? $"usb {vid}:{pid}" : null,
            snap.Serial is null ? null : $"s/n {snap.Serial}",
        }.Where(x => x is not null));
        UpdateAge(snap);

        if (_history.TryGetValue(addr.Value, out var h))
        {
            // Only hand the sparklines a new array when the points differ (a new array always redraws).
            if (ChargeHistory is null || !ChargeHistory.SequenceEqual(h.Charge)) ChargeHistory = h.Charge.ToArray();
            if (LoadHistory is null || !LoadHistory.SequenceEqual(h.Load)) LoadHistory = h.Load.ToArray();
        }
        var r = _svc.Settings.Shutdown;
        RuleText = !r.Enabled ? "Shutdown rules are off" :
            $"{r.Action} at {(r.BatteryPercentBelow > 0 ? r.BatteryPercentBelow + "% battery" : "")}" +
            $"{(r.BatteryPercentBelow > 0 && r.RuntimeSecondsBelow > 0 ? " or " : "")}" +
            $"{(r.RuntimeSecondsBelow > 0 ? ShutdownCoordinator.Fmt(r.RuntimeSecondsBelow) + " runtime" : "")}" +
            $"{(r.OnLowBatteryFlag ? ", or when the UPS flags low battery" : "")}";
    }

    public void Tick()
    {
        var addr = _selected ?? _svc.PrimaryAddress;
        if (addr is { } a && _svc.Snapshots.TryGetValue(a, out var s)) UpdateAge(s);
    }

    private void UpdateAge(UpsSnapshot s)
    {
        var age = DateTimeOffset.UtcNow - s.Timestamp;
        // Coarse on purpose: a per-second "3s ago" counter redrew the window every second for nothing.
        UpdatedText = AgeText(s.Stale, age);
    }

    public static string AgeText(bool stale, TimeSpan age) =>
        stale ? "stale" :
        age.TotalSeconds < 15 ? "updated just now" :
        age.TotalSeconds < 60 ? "updated under a minute ago" :
        age.TotalMinutes < 60 ? $"updated {(int)age.TotalMinutes} min ago" : "updated over an hour ago";

    private string StatusLine()
    {
        var st = _svc.Monitors.Select(m => m.Status).FirstOrDefault(s => s.Message is not null);
        if (st?.Message is { } msg)
            return st.State == ConnectionState.Reconnecting ? $"{msg} {ConnectionText.Describe(st, DateTimeOffset.UtcNow)}." : msg;
        return "Waiting for the first reading…";
    }

    private void SetPill(string text, Color c, bool pulse)
    {
        PillText = text;
        PillBrush = Palette.Brush(c);
        PillSoft = Palette.Soft(c);
        PillPulse = pulse;
    }
}
