using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpsHub.App.Services;
using UpsHub.Core;
using UpsHub.Core.Model;
using UpsHub.Core.Monitoring;

namespace UpsHub.App.ViewModels;

public sealed partial class SidebarUpsViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private IBrush _dot = Palette.Brush(Palette.Subtle);
    [ObservableProperty] private string _value = "";
}

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly DispatcherTimer _clock;
    public UpsHubService Service { get; }
    public DashboardViewModel Dashboard { get; }
    public ServersViewModel Servers { get; }
    public ShutdownViewModel Shutdown { get; }
    public SettingsViewModel Settings { get; }
    public CountdownViewModel Countdown { get; }

    public string AppName => AppInfo.DisplayName;
    public string Tagline => AppInfo.Tagline.ToUpperInvariant();
    public string Version => "v" + AppInfo.Version;
    public bool IsDemo => Service.IsDemo;

    public ObservableCollection<SidebarUpsViewModel> SidebarUps { get; } = new();

    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private string _page = "dashboard";
    [ObservableProperty] private string _footerText = "No servers";
    [ObservableProperty] private IBrush _footerBrush = Palette.Brush(Palette.Subtle);
    [ObservableProperty] private IBrush _footerSoft = Palette.Soft(Palette.Subtle);
    [ObservableProperty] private string _trayText = AppInfo.DisplayName;
    [ObservableProperty] private UpsHealth _overallHealth = UpsHealth.Unknown;

    public bool IsDashboard => Page == "dashboard";
    public bool IsServers => Page == "servers";
    public bool IsShutdown => Page == "shutdown";
    public bool IsSettings => Page == "settings";

    public event Action<UpsHealth, string>? TrayStateChanged;

    public MainViewModel(UpsHubService svc)
    {
        Service = svc;
        Dashboard = new DashboardViewModel(svc, this);
        Servers = new ServersViewModel(svc);
        Shutdown = new ShutdownViewModel(svc);
        Settings = new SettingsViewModel(svc);
        Settings.RunSetup = () => OpenWizard();
        Countdown = new CountdownViewModel(svc);
        _currentPage = Dashboard;
        svc.Changed += Refresh;
        svc.ShutdownStateChanged += st => Countdown.Update(st);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { Dashboard.Tick(); Servers.Refresh(); });
        _clock.Start();
        Refresh();
    }

    /// <summary>
    /// The 1 s clock only refreshes text on screen ("updated … ago", retry countdowns), so it stops while
    /// the window is minimized or hidden to the tray and restarts, with a fresh refresh, when it is shown.
    /// Monitoring, notifications and shutdown rules run in <see cref="UpsHubService"/> and are unaffected.
    /// </summary>
    public void SetWindowActive(bool active)
    {
        if (active == _clock.IsEnabled) return;
        if (active) { Refresh(); Dashboard.Tick(); _clock.Start(); }
        else
        {
            _clock.Stop();
            // Hidden to the tray: give back the memory the UI used (one compacting collection, not a loop).
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
        }
    }

    public bool ClockRunning => _clock.IsEnabled;

    [ObservableProperty] private UpdateViewModel? _updates;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowWizard))] private SetupWizardViewModel? _wizard;
    public bool ShowWizard => Wizard is not null;

    /// <summary>Opens the setup wizard (first run, or Settings → Run setup again).</summary>
    public void OpenWizard(Core.Platform.ILegacySettingsImporter? importer = null) =>
        Wizard = new SetupWizardViewModel(Service, CloseWizard, importer ?? WizardImporter);

    /// <summary>Demo: a stand-in WinNUT importer so the import step can be shown.</summary>
    public Core.Platform.ILegacySettingsImporter? WizardImporter { get; set; }

    private void CloseWizard()
    {
        Wizard = null;
        Navigate("dashboard");
        Refresh();
    }

    partial void OnUpdatesChanged(UpdateViewModel? value) { Dashboard.Updates = value; Settings.Updates = value; }

    [RelayCommand]
    public void Navigate(string page)
    {
        Page = page;
        CurrentPage = page switch
        {
            "servers" => Servers,
            "shutdown" => Shutdown,
            "settings" => Settings,
            _ => Dashboard,
        };
        if (page == "shutdown") Shutdown.RefreshUpsOptions();
        OnPropertyChanged(nameof(IsDashboard)); OnPropertyChanged(nameof(IsServers));
        OnPropertyChanged(nameof(IsShutdown)); OnPropertyChanged(nameof(IsSettings));
    }

    public void Refresh()
    {
        Dashboard.Refresh();
        Servers.Refresh();
        Shutdown.RefreshUpsOptions();

        var snaps = Service.Snapshots.OrderBy(k => k.Key.ToString()).ToList();
        while (SidebarUps.Count > snaps.Count) SidebarUps.RemoveAt(SidebarUps.Count - 1);
        while (SidebarUps.Count < snaps.Count) SidebarUps.Add(new SidebarUpsViewModel());
        for (int i = 0; i < snaps.Count; i++)
        {
            var (a, s) = (snaps[i].Key, snaps[i].Value);
            var connected = Service.IsConnected(a);
            SidebarUps[i].Name = a.Ups;
            SidebarUps[i].Host = Service.SourceLabelOf(a) ?? a.Host;
            SidebarUps[i].Dot = Palette.Brush(connected ? Palette.For(s.Health) : Palette.Degraded);
            SidebarUps[i].Value = s.BatteryCharge is { } c ? $"{c:0}%" : "";
        }

        var servers = Service.Monitors.ToList();
        var up = servers.Count(m => m.Status.State == ConnectionState.Connected);
        var primary = Service.PrimarySnapshot;
        var health = primary?.Health ?? UpsHealth.Unknown;
        if (servers.Count == 0) SetFooter("No UPS sources", Palette.Subtle);
        else if (up < servers.Count) SetFooter($"{up} of {servers.Count} connected", Palette.Degraded);
        else SetFooter(health switch
        {
            UpsHealth.OnBattery => "On battery",
            UpsHealth.LowBattery => "Low battery",
            UpsHealth.ForcedShutdown => "Shutting down",
            _ => $"{up} of {servers.Count} live",
        }, health is UpsHealth.Unknown or UpsHealth.Online ? Palette.Up : Palette.For(health));

        if (up < servers.Count && servers.Count > 0) health = UpsHealth.Unknown;
        OverallHealth = health;
        var tray = primary is null ? $"{AppInfo.DisplayName} · {FooterText}" :
            $"{AppInfo.DisplayName} · {Dashboard.PillText} · {primary.BatteryCharge?.ToString("0") ?? "–"}% · {Dashboard.RuntimeText} {Dashboard.RuntimeUnit}";
        TrayText = tray.Length > 120 ? tray[..120] : tray;   // NotifyIcon text limit is 127 chars (WinNUT de-DE crash)
        TrayStateChanged?.Invoke(OverallHealth, TrayText);
    }

    private void SetFooter(string text, Avalonia.Media.Color c)
    {
        FooterText = text;
        FooterBrush = Palette.Brush(c);
        FooterSoft = Palette.Soft(c);
    }
}
