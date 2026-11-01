using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpsHub.App.Services;
using UpsHub.Core;
using UpsHub.Core.Settings;

namespace UpsHub.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly UpsHubService _svc;
    private bool _loading;
    private GeneralSettings G => _svc.Settings.General;

    public SettingsViewModel(UpsHubService svc)
    {
        _svc = svc;
        _loading = true;
        StartWithWindows = G.StartWithWindows; StartMinimized = G.StartMinimized; CloseToTray = G.CloseToTray;
        Notifications = G.Notifications; NotifyPower = G.NotifyOnPowerEvents; NotifyConnection = G.NotifyOnConnectionEvents;
        PollInterval = G.PollIntervalSeconds; PowerFactor = (decimal)G.PowerFactor;
        _loading = false;
    }

    public string AppName => AppInfo.DisplayName;
    public string Version => "v" + AppInfo.Version;
    public string Tagline => AppInfo.Tagline;
    public string TaglineUpper => AppInfo.Tagline.ToUpperInvariant();
    public string SettingsPath => _svc.Store.FilePath;
    public bool StartupSupported => _svc.Platform.Startup.IsSupported;
    public bool IsWindows => _svc.Platform.IsWindows;
    public string StartupNote => StartupSupported ? $"Adds {AppInfo.DisplayName} to your sign-in apps (current user, no admin)." : "Available on Windows.";

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _notifications;
    [ObservableProperty] private bool _notifyPower;
    [ObservableProperty] private bool _notifyConnection;
    [ObservableProperty] private decimal _pollInterval;
    [ObservableProperty] private decimal _powerFactor;
    [ObservableProperty] private string _importResult = "";
    [ObservableProperty] private UpdateViewModel? _updates;
    public Action? RunSetup { get; set; }
    [RelayCommand] private void RunSetupAgain() => RunSetup?.Invoke();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(ImportResult) or nameof(Updates)) return;
        G.StartWithWindows = StartWithWindows; G.StartMinimized = StartMinimized; G.CloseToTray = CloseToTray;
        G.Notifications = Notifications; G.NotifyOnPowerEvents = NotifyPower; G.NotifyOnConnectionEvents = NotifyConnection;
        G.PollIntervalSeconds = (int)PollInterval; G.PowerFactor = (double)PowerFactor;
        _svc.ApplySettings();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_svc.Store.Directory);
            Process.Start(new ProcessStartInfo(_svc.Store.Directory) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void ImportWinNut()
    {
        var before = _svc.Settings.Servers.Count;
        var msg = _svc.Platform.LegacyImporter.TryImport(_svc.Settings);
        ImportResult = msg ?? "No WinNUT settings found in the registry.";
        if (msg is not null) { _svc.Save(); if (_svc.Settings.Servers.Count != before) _svc.RebuildMonitors(); }
    }

    [RelayCommand]
    private void OpenRepo()
    {
        if (string.IsNullOrEmpty(AppInfo.RepoUrl)) return;
        try { Process.Start(new ProcessStartInfo(AppInfo.RepoUrl) { UseShellExecute = true }); } catch { }
    }
}
