using Avalonia.Threading;
using UpsHub.App.Platform;
using UpsHub.Core;
using UpsHub.Core.Discovery;
using UpsHub.Core.Local;
using UpsHub.Core.Model;
using UpsHub.Core.Monitoring;
using UpsHub.Core.Nut;
using UpsHub.Core.Platform;
using UpsHub.Core.Settings;
using UpsHub.Core.Shutdown;

namespace UpsHub.App.Services;

/// <summary>
/// The app's engine: owns settings, one monitor per source (a NUT <see cref="ServerMonitor"/> or a
/// <see cref="LocalUpsMonitor"/> for a UPS plugged into this PC), the shutdown coordinator
/// and notifications. All events are raised on the UI thread.
/// </summary>
public sealed class UpsHubService : IAsyncDisposable
{
    private readonly Dictionary<string, IUpsMonitor> _monitors = new();
    /// <summary>Which monitor publishes each UPS address (so "connected" is exact for USB and NUT alike).</summary>
    private readonly Dictionary<UpsAddress, IUpsMonitor> _owners = new();
    private readonly Dictionary<UpsAddress, UpsSnapshot> _snapshots = new();
    private readonly Dictionary<UpsAddress, UpsHealth> _lastHealth = new();
    private readonly Dictionary<string, ConnectionState> _lastConn = new();
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    public SettingsStore Store { get; }
    public AppSettings Settings { get; private set; }
    public PlatformServices Platform { get; }
    public ShutdownCoordinator Shutdown { get; }
    /// <summary>The only path that performs the power action (deadline, "now", tests).</summary>
    public ShutdownController Power { get; }
    public INotifier? InAppNotifier { get; set; }
    public bool IsDemo { get; init; }
    /// <summary>No settings file existed when UPS Hub started (fresh install).</summary>
    public bool IsFirstRun { get; }
    /// <summary>The setup wizard should open: nothing configured and setup never finished or skipped.</summary>
    public bool NeedsSetup => !Settings.SetupCompleted && Settings.Servers.Count == 0;
    public string? LastActionResult { get; private set; }

    public event Action? Changed;                         // snapshots / statuses changed
    public event Action<ShutdownState>? ShutdownStateChanged;
    public event Action<UpsAddress, UpsSnapshot>? SnapshotArrived;

    public UpsHubService(SettingsStore store, PlatformServices platform)
    {
        Store = store;
        Platform = platform;
        IsFirstRun = !File.Exists(store.FilePath);
        Settings = store.Load();
        Shutdown = new ShutdownCoordinator(Settings.Shutdown);
        Power = new ShutdownController(Shutdown, platform.Power, () => Settings.Shutdown);
        Power.ActionCompleted += (msg, simulated) =>
        {
            LastActionResult = msg;
            if (simulated) Notify("Shutdown (simulated)", msg, NotificationKind.Info);
        };
        Power.ActionFailed += ex => { LastActionResult = ex.Message; Notify("Shutdown failed", ex.Message, NotificationKind.Critical); };
        Shutdown.StateChanged += OnShutdownStateChanged;
        Platform.PowerEvents.Resumed += (_, _) => Dispatcher.UIThread.Post(OnResumed);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnTick());
    }

    public IReadOnlyDictionary<UpsAddress, UpsSnapshot> Snapshots => _snapshots;
    public IEnumerable<IUpsMonitor> Monitors => _monitors.Values;
    public IUpsMonitor? MonitorOf(UpsAddress a) => _owners.GetValueOrDefault(a);
    public ServerSettings? ServerOf(UpsAddress a) => MonitorOf(a)?.Config
        ?? (a.Host == SettingsStore.LocalHost ? null : Settings.Servers.FirstOrDefault(s => !s.IsLocal && string.Equals(s.Host, a.Host, StringComparison.OrdinalIgnoreCase) && s.Port == a.Port));

    /// <summary>"USB · APC Back-UPS BK650M2-CH" for a local UPS, null for a NUT one.</summary>
    public string? SourceLabelOf(UpsAddress a) =>
        MonitorOf(a) is { Config: { IsLocal: true } c } ? LocalNames.SourceLabel(c.Local!, _snapshots.GetValueOrDefault(a)) : null;
    public ServerStatus? StatusOf(string serverId) => _monitors.TryGetValue(serverId, out var m) ? m.Status : null;

    public void Start()
    {
        // Configs from before the wizard existed count as set up.
        if (!Settings.SetupCompleted && Settings.Servers.Count > 0) { Settings.SetupCompleted = true; Save(); }
        // A fresh install offers the WinNUT import inside the setup wizard instead of doing it silently.
        if (!Settings.LegacyImportDone && !NeedsSetup)
        {
            string? msg = null;
            try { msg = Platform.LegacyImporter.TryImport(Settings); } catch { }
            Settings.LegacyImportDone = true;
            Save();
            if (msg is not null) Notify("Settings imported", msg, NotificationKind.Success);
        }
        try
        {
            // First run: adopt what the installer set ("Start with Windows" checkbox) instead of undoing it.
            if (IsFirstRun && Platform.Startup.IsSupported) { Settings.General.StartWithWindows = Platform.Startup.IsEnabled(); Save(); }
            else if (Platform.Startup.IsSupported && Platform.Startup.IsEnabled() != Settings.General.StartWithWindows)
                Platform.Startup.SetEnabled(Settings.General.StartWithWindows);
        }
        catch { }
        RebuildMonitors();
        _timer.Start();
    }

    public void Save()
    {
        try { Store.Save(Settings); } catch (Exception ex) { Notify("Couldn't save settings", ex.Message, NotificationKind.Warning); }
    }

    /// <summary>Apply edited settings: save, restart monitors whose config changed, update rules.</summary>
    public void ApplySettings()
    {
        Save();
        Shutdown.Rules = Settings.Shutdown;
        try { if (Platform.Startup.IsSupported) Platform.Startup.SetEnabled(Settings.General.StartWithWindows); } catch { }
        foreach (var m in _monitors.Values)
        {
            m.PollInterval = TimeSpan.FromSeconds(Settings.General.PollIntervalSeconds);
            m.PowerFactor = Settings.General.PowerFactor;
        }
        Changed?.Invoke();
    }

    public void RebuildMonitors()
    {
        foreach (var m in _monitors.Values) _ = m.DisposeAsync().AsTask();
        _monitors.Clear();
        _snapshots.Clear();
        _lastHealth.Clear();
        _lastConn.Clear();
        _owners.Clear();
        var poll = TimeSpan.FromSeconds(Settings.General.PollIntervalSeconds);
        foreach (var s in Settings.Servers.Where(s => s.Enabled && (s.IsLocal || !string.IsNullOrWhiteSpace(s.Host))))
        {
            IUpsMonitor m;
            if (s.IsLocal)
            {
                var local = s.Local!;
                m = new LocalUpsMonitor(s, () => Platform.LocalUps.Create(local), poll) { PowerFactor = Settings.General.PowerFactor };
            }
            else
            {
                var pw = Platform.Secrets.Unprotect(s.ProtectedPassword);
                m = new ServerMonitor(s, pw, poll) { PowerFactor = Settings.General.PowerFactor };
            }
            var owner = m;
            m.SnapshotUpdated += (a, snap) => Dispatcher.UIThread.Post(() => { _owners[a] = owner; OnSnapshot(a, snap); });
            m.StatusChanged += st => Dispatcher.UIThread.Post(() => OnStatus(s, st));
            _monitors[s.Id] = m;
            m.Start();
        }
        Changed?.Invoke();
    }

    public UpsAddress? PrimaryAddress
    {
        get
        {
            if (UpsAddress.TryParse(Settings.Shutdown.PrimaryUps, out var a) && _snapshots.ContainsKey(a)) return a;
            if (UpsAddress.TryParse(Settings.Shutdown.PrimaryUps, out a)) return a;
            return _snapshots.Keys.OrderBy(k => k.ToString()).Cast<UpsAddress?>().FirstOrDefault();
        }
    }

    public UpsSnapshot? PrimarySnapshot => PrimaryAddress is { } a && _snapshots.TryGetValue(a, out var s) ? s : null;

    public bool IsConnected(UpsAddress a) =>
        _owners.TryGetValue(a, out var o) ? o.Status.State == ConnectionState.Connected
        : _monitors.Values.Any(m => !m.Config.IsLocal && string.Equals(m.Config.Host, a.Host, StringComparison.OrdinalIgnoreCase)
                                    && m.Config.Port == a.Port && m.Status.State == ConnectionState.Connected);

    /// <summary>Friendly name for notifications: "APC Back-UPS BK650M2-CH (USB)" or the NUT address.</summary>
    public string NameOf(UpsAddress a) =>
        MonitorOf(a) is { Config: { IsLocal: true } c }
            ? (string.IsNullOrWhiteSpace(c.DisplayName) ? LocalNames.DisplayName(c.Local!.Vendor, c.Local.Model) : c.DisplayName) + " (USB)"
            : a.ToString();

    internal void OnSnapshot(UpsAddress a, UpsSnapshot snap)
    {
        _snapshots[a] = snap;
        var health = snap.Health;
        var prev = _lastHealth.GetValueOrDefault(a, UpsHealth.Unknown);
        _lastHealth[a] = health;
        if (health != prev && prev != UpsHealth.Unknown && Settings.General.NotifyOnPowerEvents)
        {
            var name = NameOf(a);
            switch (health)
            {
                case UpsHealth.OnBattery: Notify("On battery", $"{name} lost mains power. {Describe(snap)}", NotificationKind.Warning); break;
                case UpsHealth.LowBattery: Notify("Low battery", $"{name} reports low battery. {Describe(snap)}", NotificationKind.Critical); break;
                case UpsHealth.ForcedShutdown: Notify("Forced shutdown", $"{name}: the NUT server ordered a shutdown (FSD).", NotificationKind.Critical); break;
                case UpsHealth.Online when prev is UpsHealth.OnBattery or UpsHealth.LowBattery:
                    Notify("Power restored", $"{name} is back on mains power.", NotificationKind.Success); break;
            }
        }
        SnapshotArrived?.Invoke(a, snap);
        EvaluateShutdown();
        Changed?.Invoke();
    }

    private static string Describe(UpsSnapshot s)
    {
        var parts = new List<string>();
        if (s.BatteryCharge is { } c) parts.Add($"Battery {c:0}%");
        if (s.BatteryRuntimeSeconds is { } r) parts.Add($"{ShutdownCoordinator.Fmt(r)} left");
        return string.Join(", ", parts) + (parts.Count > 0 ? "." : "");
    }

    private void OnStatus(ServerSettings s, ServerStatus st)
    {
        var prev = _lastConn.GetValueOrDefault(s.Id, ConnectionState.Stopped);
        _lastConn[s.Id] = st.State;
        var quiet = DateTimeOffset.UtcNow < _quietUntil;
        if (!quiet && Settings.General.NotifyOnConnectionEvents && prev != st.State)
        {
            var label = !string.IsNullOrWhiteSpace(s.DisplayName) ? s.DisplayName
                : s.IsLocal ? $"the USB UPS ({LocalNames.DisplayName(s.Local!.Vendor, s.Local.Model)})" : s.Host;
            if (prev == ConnectionState.Connected && st.State == ConnectionState.Reconnecting)
                Notify("Connection lost", s.IsLocal
                    ? $"Lost contact with {label}. Check the USB cable; retrying automatically."
                    : $"Lost contact with {label}. Retrying automatically.", NotificationKind.Warning);
            else if (prev == ConnectionState.Reconnecting && st.State == ConnectionState.Connected)
                Notify("Reconnected", $"Back in contact with {label}.", NotificationKind.Success);
        }
        EvaluateShutdown();
        Changed?.Invoke();
    }

    private void EvaluateShutdown()
    {
        var a = PrimaryAddress;
        UpsSnapshot? snap = a is { } addr ? _snapshots.GetValueOrDefault(addr) : null;
        var connected = a is { } ad && IsConnected(ad);
        Power.Evaluate(snap, connected, DateTimeOffset.UtcNow);   // ignored while a test countdown runs
    }

    private async void OnTick()
    {
        if (Shutdown.State.Phase == ShutdownPhase.CountingDown)
            ShutdownStateChanged?.Invoke(Shutdown.State);  // drives the countdown UI
        await Power.TickAsync(DateTimeOffset.UtcNow);
    }

    public Task ShutdownNowAsync() => Power.NowAsync();

    public void CancelShutdown() => Power.Cancel();

    /// <summary>
    /// Starts a test countdown that ends with the REAL configured action (shut down / hibernate / sleep),
    /// unless power actions are simulated (demo mode). The UI asks for confirmation first.
    /// </summary>
    public bool StartShutdownTest() => Power.StartTest(DateTimeOffset.UtcNow);
    public bool TestMode => Power.IsTest;

    private void OnShutdownStateChanged(ShutdownState old, ShutdownState now)
    {
        // 4 Hz only while a countdown is on screen; otherwise once a second is plenty.
        _timer.Interval = now.Phase == ShutdownPhase.CountingDown ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);
        if (old.Phase != ShutdownPhase.CountingDown && now.Phase == ShutdownPhase.CountingDown)
            Notify(TestMode ? $"Shutdown test: {Settings.Shutdown.Action.ToString().ToLowerInvariant()} in {ShutdownCoordinator.Fmt(now.Remaining(DateTimeOffset.UtcNow).TotalSeconds)}"
                            : $"{Settings.Shutdown.Action} in {ShutdownCoordinator.Fmt(now.Remaining(DateTimeOffset.UtcNow).TotalSeconds)}",
                now.Reason ?? "", NotificationKind.Critical);
        if (old.Phase == ShutdownPhase.CountingDown && now.Phase == ShutdownPhase.Idle && now.LastCancelReason == "Power restored")
            Notify("Shutdown cancelled", "Power restored.", NotificationKind.Success);
        ShutdownStateChanged?.Invoke(now);
    }

    private void OnResumed()
    {
        // Network needs a moment after resume; reconnect quietly (nutdotnet #233, #145).
        _quietUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        foreach (var m in _monitors.Values) m.Kick(reconnect: true);
    }

    public void Notify(string title, string message, NotificationKind kind = NotificationKind.Info)
    {
        if (!Settings.General.Notifications && kind != NotificationKind.Critical) return;
        // One channel only: Windows toasts when available, otherwise in-app cards.
        if (Platform.SystemNotifier is { } sys) sys.Show(title, message, kind);
        else InAppNotifier?.Show(title, message, kind);
    }

    // ---- Discovery -------------------------------------------------------------------------

    /// <summary>Creates the scanner (demo mode swaps in a simulated network).</summary>
    public Func<NutDiscovery> DiscoveryFactory { get; set; } = NutDiscovery.ForThisMachine;
    /// <summary>Demo / screenshot hook to adjust the options before a scan.</summary>
    public Action<DiscoveryOptions>? DiscoveryTweak { get; set; }

    public DiscoveryOptions BuildDiscoveryOptions()
    {
        var d = Settings.Discovery;
        var saved = Settings.Servers.Select(x => $"{x.Host}:{x.Port}").Concat(d.KnownHosts)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var o = new DiscoveryOptions
        {
            Ports = TargetSpec.ParsePorts(d.ExtraPorts),
            UseMdns = d.UseMdns,
            IncludeVirtualAdapters = d.IncludeVirtualAdapters,
            CustomTargets = d.CustomTargets,
            SavedHosts = saved,
        };
        DiscoveryTweak?.Invoke(o);
        return o;
    }

    /// <summary>Already configured? Matches by host, any of its addresses, or its host name, on the same port.</summary>
    public ServerSettings? FindConfigured(DiscoveredServer d) =>
        Settings.Servers.FirstOrDefault(x => x.Port == d.Port && (
            string.Equals(x.Host, d.Host, StringComparison.OrdinalIgnoreCase)
            || (d.HostName is not null && string.Equals(x.Host, d.HostName, StringComparison.OrdinalIgnoreCase))
            || (System.Net.IPAddress.TryParse(x.Host, out var ip) && d.Addresses.Contains(ip))
            || (d.IsLocalMachine && x.Host is "localhost" or "127.0.0.1" or "::1")));

    /// <summary>Remembers found servers so the next scan probes them first.</summary>
    public void RememberDiscovered(IEnumerable<DiscoveredServer> found)
    {
        var known = Settings.Discovery.KnownHosts;
        bool changed = false;
        foreach (var s in found)
        {
            var key = $"{s.Host}:{s.Port}";
            if (known.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            known.Add(key); changed = true;
        }
        while (known.Count > 64) { known.RemoveAt(0); changed = true; }
        if (changed) Save();
    }

    public ServerSettings AddDiscovered(DiscoveredServer d, string? username, string? password)
    {
        var s = new ServerSettings
        {
            Host = d.Host,
            Port = d.Port,
            DisplayName = d.MdnsInstance ?? (d.HostName is { } h && h != "This PC" ? h.Split('.')[0] : d.IsLocalMachine ? "This PC" : ""),
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            Tls = d.TlsSupported == true ? TlsMode.Prefer : TlsMode.Off,
        };
        if (!string.IsNullOrEmpty(password)) s.ProtectedPassword = Platform.Secrets.Protect(password);
        Settings.Servers.Add(s);
        Save();
        RebuildMonitors();
        return s;
    }

    // ---- Local USB UPS ---------------------------------------------------------------------

    /// <summary>Lists UPSes plugged into this PC. Runs on a worker thread (HID/serial probing can block).</summary>
    public Task<IReadOnlyList<LocalDeviceInfo>> FindLocalAsync(bool probeSerial) =>
        Task.Run(() => Platform.LocalUps.Enumerate(probeSerial));

    public ServerSettings? FindConfigured(LocalDeviceInfo d) =>
        Settings.Servers.FirstOrDefault(x => x.IsLocal && x.Local!.Tier == d.Tier && x.Local.DeviceKey == d.Key);

    public ServerSettings AddLocal(LocalDeviceInfo d)
    {
        var s = new ServerSettings
        {
            Kind = UpsSourceKind.Local,
            Host = SettingsStore.LocalHost,
            DisplayName = "",
            Local = d.ToSettings(),
        };
        // Keep addresses unique if several UPSes of the same model are plugged in.
        var baseName = LocalNames.UpsName(d.Vendor, d.Model);
        var name = baseName;
        for (int i = 2; Settings.Servers.Any(x => x.IsLocal && (x.UpsNames.FirstOrDefault() ?? LocalNames.UpsName(x.Local!.Vendor, x.Local.Model)) == name); i++)
            name = baseName + i;
        s.UpsNames.Add(name);
        Settings.Servers.Add(s);
        if (string.IsNullOrWhiteSpace(Settings.Shutdown.PrimaryUps) || Settings.Servers.Count == 1)
            Settings.Shutdown.PrimaryUps = $"{name}@{SettingsStore.LocalHost}:{s.Port}";
        Save();
        RebuildMonitors();
        return s;
    }

    /// <summary>Connects once with the given settings and returns server version + UPS list (for the Add server form).</summary>
    public static async Task<(string? Version, List<NutUpsInfo> Ups)> ProbeAsync(ServerSettings s, string? password, CancellationToken ct = default)
    {
        await using var c = new NutClient(new NutConnectOptions { Host = s.Host, Port = s.Port, Tls = s.Tls });
        await c.ConnectAsync(ct);
        string? ver = null;
        try { ver = await c.GetServerVersionAsync(ct); } catch (NutException) { }
        await c.AuthenticateAsync(s.Username, password, ct);
        var list = await c.ListUpsAsync(ct);
        return (ver, list);
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        foreach (var m in _monitors.Values) await m.DisposeAsync();
        (Platform.PowerEvents as IDisposable)?.Dispose();
    }
}
