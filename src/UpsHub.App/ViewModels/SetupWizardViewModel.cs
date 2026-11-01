using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpsHub.App.Services;
using UpsHub.Core;
using UpsHub.Core.Local;
using UpsHub.Core.Nut;
using UpsHub.Core.Platform;
using UpsHub.Core.Settings;

namespace UpsHub.App.ViewModels;

public enum SetupStep { Welcome, Connect, Shutdown, Startup, Done }
public enum ConnectMode { Auto, Usb, Manual, Import }

/// <summary>
/// First-time setup: welcome → connect a UPS (scan / USB / manual / import from WinNUT) → shutdown
/// basics → start with Windows + notifications → done. Re-runnable from Settings.
/// Connections are added to the live settings as the user picks them (like the Servers page);
/// the rest is written on Finish.
/// </summary>
public sealed partial class SetupWizardViewModel : ViewModelBase
{
    private readonly UpsHubService _svc;
    private readonly ILegacySettingsImporter _importer;
    private readonly Action _close;
    private AppSettings? _importPreview;

    public SetupWizardViewModel(UpsHubService svc, Action close, ILegacySettingsImporter? importer = null)
    {
        _svc = svc;
        _close = close;
        _importer = importer ?? svc.Platform.LegacyImporter;
        Discovery = new DiscoveryViewModel(svc, OnAdded);
        Local = new LocalUpsViewModel(svc, OnAdded);
        StartupSupported = svc.Platform.Startup.IsSupported;
        PowerReal = svc.Power.PerformsRealActions;

        // Preview a WinNUT import into a scratch copy: nothing is changed until the user applies it.
        try
        {
            var scratch = new AppSettings();
            var summary = _importer.TryImport(scratch);
            if (summary is not null) { _importPreview = scratch; BuildImportPreview(scratch); }
        }
        catch { _importPreview = null; }
        HasWinNut = _importPreview is not null;
        LoadRules();
        LoadGeneral();
        RefreshConnected();
    }

    public DiscoveryViewModel Discovery { get; }
    public LocalUpsViewModel Local { get; }
    public string AppName => AppInfo.DisplayName;
    public bool StartupSupported { get; }
    public bool PowerReal { get; }
    public int StepCount => 5;

    [ObservableProperty] private SetupStep _step = SetupStep.Welcome;
    [ObservableProperty] private ConnectMode _mode = ConnectMode.Auto;
    [ObservableProperty] private bool _hasWinNut;

    // ---- step flags for the view ----
    public int StepNumber => (int)Step + 1;
    public bool IsWelcome => Step == SetupStep.Welcome;
    public bool IsConnect => Step == SetupStep.Connect;
    public bool IsShutdownStep => Step == SetupStep.Shutdown;
    public bool IsStartup => Step == SetupStep.Startup;
    public bool IsDone => Step == SetupStep.Done;
    public bool CanBack => Step is not (SetupStep.Welcome or SetupStep.Done);
    public bool ShowNav => Step is not (SetupStep.Welcome or SetupStep.Done);
    public string NextText => Step switch
    {
        SetupStep.Connect => ConnectedCount == 0 ? "Skip for now" : "Continue",
        SetupStep.Startup => "Finish setup",
        _ => "Continue",
    };
    public bool Step1Done => Step > SetupStep.Welcome;
    public bool Step2Done => Step > SetupStep.Connect;
    public bool Step3Done => Step > SetupStep.Shutdown;
    public bool Step4Done => Step > SetupStep.Startup;
    public bool IsAuto => Mode == ConnectMode.Auto;
    public bool IsUsb => Mode == ConnectMode.Usb;
    public bool IsManual => Mode == ConnectMode.Manual;
    public bool IsImport => Mode == ConnectMode.Import;

    partial void OnStepChanged(SetupStep value)
    {
        foreach (var n in new[] { nameof(StepNumber), nameof(IsWelcome), nameof(IsConnect), nameof(IsShutdownStep), nameof(IsStartup),
                     nameof(IsDone), nameof(CanBack), nameof(ShowNav), nameof(NextText), nameof(Step1Done), nameof(Step2Done),
                     nameof(Step3Done), nameof(Step4Done) })
            OnPropertyChanged(n);
        if (value == SetupStep.Shutdown) LoadRules();      // an import may have changed them
        if (value == SetupStep.Startup) LoadGeneral();
    }

    partial void OnModeChanged(ConnectMode value)
    {
        OnPropertyChanged(nameof(IsAuto)); OnPropertyChanged(nameof(IsUsb)); OnPropertyChanged(nameof(IsManual)); OnPropertyChanged(nameof(IsImport));
        if (value == ConnectMode.Usb && !Local.Scanned && !Local.Scanning) _ = Local.ScanAsync();
    }

    [RelayCommand] private void SetMode(string mode) => Mode = Enum.Parse<ConnectMode>(mode, true);

    [RelayCommand]
    private void Begin()
    {
        Step = SetupStep.Connect;
        if (Mode == ConnectMode.Auto && !Discovery.HasRun && !Discovery.IsScanning) _ = Discovery.StartAsync();
    }

    [RelayCommand]
    private void Next()
    {
        if (Step == SetupStep.Startup) { Finish(); return; }
        if (Step < SetupStep.Done) Step++;
    }

    [RelayCommand] private void Back() { if (CanBack) Step--; }

    /// <summary>"Set up later": keep what was added, don't show the wizard again.</summary>
    [RelayCommand]
    private void Later()
    {
        Discovery.CancelCommand.Execute(null);
        _svc.Settings.SetupCompleted = true;
        _svc.Settings.LegacyImportDone = true;
        _svc.Save();
        _close();
    }

    [RelayCommand] private void Close() => _close();

    // ---- connect: what's configured so far ----
    public ObservableCollection<string> Connected { get; } = new();
    [ObservableProperty] private int _connectedCount;
    public bool HasConnected => ConnectedCount > 0;
    partial void OnConnectedCountChanged(int value) { OnPropertyChanged(nameof(HasConnected)); OnPropertyChanged(nameof(NextText)); }

    private void OnAdded()
    {
        Discovery.RefreshAdded();
        Local.RefreshAdded();
        RefreshConnected();
    }

    public void RefreshConnected()
    {
        Connected.Clear();
        foreach (var s in _svc.Settings.Servers)
            Connected.Add(s.IsLocal
                ? $"{LocalNames.DisplayName(s.Local!.Vendor, s.Local.Model)} · USB on this PC"
                : $"{(string.IsNullOrWhiteSpace(s.DisplayName) ? s.Host : s.DisplayName)} · {s.Host}:{s.Port}");
        ConnectedCount = Connected.Count;
    }

    // ---- connect: manual ----
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private decimal _port = 3493;
    [ObservableProperty] private string _upsName = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _manualResult = "";
    [ObservableProperty] private bool _manualOk;
    [ObservableProperty] private bool _manualBusy;

    private ServerSettings ManualDraft() => new()
    {
        Host = Host.Trim(),
        Port = (int)Port,
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        UpsNames = string.IsNullOrWhiteSpace(UpsName) ? new() : UpsName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
    };

    [RelayCommand]
    private async Task TestManualAsync()
    {
        if (string.IsNullOrWhiteSpace(Host)) { ManualOk = false; ManualResult = "Enter the NUT server's host name or IP address."; return; }
        ManualBusy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var (_, ups) = await UpsHubService.ProbeAsync(ManualDraft(), string.IsNullOrEmpty(Password) ? null : Password, cts.Token);
            ManualOk = true;
            ManualResult = ups.Count == 0 ? "Connected, but the server lists no UPS." : $"Connected. Found {string.Join(", ", ups.Select(u => u.Name))}.";
            if (string.IsNullOrWhiteSpace(UpsName) && ups.Count == 1) UpsName = ups[0].Name;
        }
        catch (NutException ex) { ManualOk = false; ManualResult = NutErrorParser.Describe(ex.Error); }
        catch (Exception ex) { ManualOk = false; ManualResult = ex.Message; }
        finally { ManualBusy = false; }
    }

    [RelayCommand]
    private void AddManual()
    {
        if (string.IsNullOrWhiteSpace(Host)) { ManualOk = false; ManualResult = "Enter the NUT server's host name or IP address."; return; }
        var s = ManualDraft();
        if (_svc.Settings.Servers.Any(x => !x.IsLocal && string.Equals(x.Host, s.Host, StringComparison.OrdinalIgnoreCase) && x.Port == s.Port))
        { ManualOk = false; ManualResult = $"{s.Host}:{s.Port} is already added."; return; }
        if (!string.IsNullOrEmpty(Password)) s.ProtectedPassword = _svc.Platform.Secrets.Protect(Password);
        _svc.Settings.Servers.Add(s);
        _svc.Save();
        _svc.RebuildMonitors();
        ManualOk = true; ManualResult = $"Added {s.Host}:{s.Port}.";
        Host = ""; UpsName = ""; Username = ""; Password = ""; Port = 3493;
        OnAdded();
    }

    // ---- connect: import from WinNUT ----
    public ObservableCollection<string> ImportPreview { get; } = new();
    [ObservableProperty] private string _importUsername = "";
    [ObservableProperty] private string _importPassword = "";
    [ObservableProperty] private bool _imported;
    [ObservableProperty] private string _importResult = "";
    public bool ImportHasServer => _importPreview?.Servers.Count > 0;

    private void BuildImportPreview(AppSettings s)
    {
        ImportPreview.Clear();
        foreach (var srv in s.Servers)
            ImportPreview.Add($"NUT server  {srv.Host}:{srv.Port}" + (srv.UpsNames.Count > 0 ? $"  ·  UPS {string.Join(", ", srv.UpsNames)}" : "  ·  all UPSes"));
        var r = s.Shutdown;
        ImportPreview.Add($"Shutdown rules  {ActionName(r.Action)} below {r.BatteryPercentBelow}% or {ShutdownCoordinatorFmt(r.RuntimeSecondsBelow)} runtime, {r.CountdownSeconds} s warning");
        ImportPreview.Add($"Window  start minimized {(s.General.StartMinimized ? "on" : "off")}, close to tray {(s.General.CloseToTray ? "on" : "off")}, start with Windows {(s.General.StartWithWindows ? "on" : "off")}");
        ImportPreview.Add($"Polling  every {s.General.PollIntervalSeconds} s");
    }

    private static string ShutdownCoordinatorFmt(int s) => Core.Shutdown.ShutdownCoordinator.Fmt(s);

    [RelayCommand]
    private void ApplyImport()
    {
        if (_importPreview is null || Imported) return;
        var before = _svc.Settings.Servers.Count;
        var msg = _importer.TryImport(_svc.Settings);
        // WinNUT stores the NUT password encrypted with a machine key, so it is asked for here instead.
        foreach (var s in _svc.Settings.Servers.Skip(before))
        {
            if (!string.IsNullOrWhiteSpace(ImportUsername)) s.Username = ImportUsername.Trim();
            if (!string.IsNullOrEmpty(ImportPassword)) s.ProtectedPassword = _svc.Platform.Secrets.Protect(ImportPassword);
        }
        _svc.Settings.LegacyImportDone = true;
        _svc.Save();
        _svc.RebuildMonitors();
        Imported = true;
        ImportPassword = "";
        ImportResult = (msg ?? "Nothing to import.").Replace(" Re-enter the NUT password if your server needs one.", "");
        OnAdded();
    }

    // ---- shutdown basics ----
    [ObservableProperty] private bool _rulesEnabled;
    [ObservableProperty] private ShutdownAction _action;
    [ObservableProperty] private decimal _batteryBelow;
    [ObservableProperty] private decimal _runtimeMinutesBelow;
    [ObservableProperty] private decimal _countdownSeconds;
    public bool IsActShutdown { get => Action == ShutdownAction.Shutdown; set { if (value) Action = ShutdownAction.Shutdown; } }
    public bool IsActHibernate { get => Action == ShutdownAction.Hibernate; set { if (value) Action = ShutdownAction.Hibernate; } }
    public bool IsActSleep { get => Action == ShutdownAction.Sleep; set { if (value) Action = ShutdownAction.Sleep; } }
    partial void OnActionChanged(ShutdownAction value)
    {
        OnPropertyChanged(nameof(IsActShutdown)); OnPropertyChanged(nameof(IsActHibernate)); OnPropertyChanged(nameof(IsActSleep));
        OnPropertyChanged(nameof(RuleSummary));
    }
    partial void OnBatteryBelowChanged(decimal value) => OnPropertyChanged(nameof(RuleSummary));
    partial void OnRuntimeMinutesBelowChanged(decimal value) => OnPropertyChanged(nameof(RuleSummary));
    partial void OnCountdownSecondsChanged(decimal value) => OnPropertyChanged(nameof(RuleSummary));
    partial void OnRulesEnabledChanged(bool value) => OnPropertyChanged(nameof(RuleSummary));

    [RelayCommand] private void SetAction(string a) => Action = Enum.Parse<ShutdownAction>(a, true);

    public static string ActionName(ShutdownAction a) => a switch { ShutdownAction.Hibernate => "hibernate", ShutdownAction.Sleep => "sleep", _ => "shut down" };

    public string RuleSummary => !RulesEnabled
        ? "UPS Hub will only notify you. Nothing is shut down automatically."
        : $"On battery, UPS Hub will {ActionName(Action)} this PC when the charge drops below {BatteryBelow:0}%"
          + (RuntimeMinutesBelow > 0 ? $" or the runtime below {RuntimeMinutesBelow:0.#} min" : "")
          + $", or when the UPS reports low battery, after a {CountdownSeconds:0} s warning you can cancel.";

    private void LoadRules()
    {
        var r = _svc.Settings.Shutdown;
        RulesEnabled = r.Enabled; Action = r.Action; BatteryBelow = r.BatteryPercentBelow;
        RuntimeMinutesBelow = Math.Round(r.RuntimeSecondsBelow / 60m, 1); CountdownSeconds = r.CountdownSeconds;
    }

    [RelayCommand]
    private void ResetRules()
    {
        var d = new ShutdownRules();
        RulesEnabled = d.Enabled; Action = d.Action; BatteryBelow = d.BatteryPercentBelow;
        RuntimeMinutesBelow = d.RuntimeSecondsBelow / 60m; CountdownSeconds = d.CountdownSeconds;
    }

    // ---- startup & notifications ----
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _notifications;
    [ObservableProperty] private bool _notifyPower;

    private void LoadGeneral()
    {
        var g = _svc.Settings.General;
        StartWithWindows = StartupSupported && g.StartWithWindows;
        StartMinimized = g.StartMinimized; Notifications = g.Notifications; NotifyPower = g.NotifyOnPowerEvents;
    }

    // ---- finish ----
    public string DoneSummary { get; private set; } = "";

    public void Finish()
    {
        var r = _svc.Settings.Shutdown;
        r.Enabled = RulesEnabled; r.Action = Action;
        r.BatteryPercentBelow = (int)Math.Clamp(BatteryBelow, 0, 100);
        r.RuntimeSecondsBelow = (int)Math.Max(0, RuntimeMinutesBelow * 60);
        r.CountdownSeconds = (int)Math.Clamp(CountdownSeconds, 0, 3600);
        var g = _svc.Settings.General;
        g.StartWithWindows = StartupSupported && StartWithWindows;
        g.StartMinimized = StartMinimized; g.Notifications = Notifications; g.NotifyOnPowerEvents = NotifyPower;
        _svc.Settings.SetupCompleted = true;
        _svc.Settings.LegacyImportDone = true;
        _svc.Settings.Discovery.FirstRunPromptDone = true;
        _svc.ApplySettings();
        try { if (StartupSupported) _svc.Platform.Startup.SetEnabled(g.StartWithWindows); } catch { }
        DoneSummary = (ConnectedCount == 0 ? "No UPS connected yet. Add one from Servers when you're ready." :
                          $"Monitoring {ConnectedCount} connection{(ConnectedCount == 1 ? "" : "s")}.")
                      + " " + (RulesEnabled ? $"Shutdown rules on ({ActionName(Action)} below {BatteryBelow:0}%)." : "Shutdown rules off.")
                      + (g.StartWithWindows ? " Starts with Windows." : "");
        OnPropertyChanged(nameof(DoneSummary));
        Step = SetupStep.Done;
    }
}
