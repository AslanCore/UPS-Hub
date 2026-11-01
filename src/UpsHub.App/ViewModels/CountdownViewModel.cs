using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpsHub.App.Services;
using UpsHub.Core.Settings;
using UpsHub.Core.Shutdown;

namespace UpsHub.App.ViewModels;

public sealed partial class CountdownViewModel : ViewModelBase
{
    private readonly UpsHubService _svc;
    public CountdownViewModel(UpsHubService svc) => _svc = svc;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _timeText = "00:00";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _heading = "";
    [ObservableProperty] private bool _canExtend;
    [ObservableProperty] private string _extendText = "";
    [ObservableProperty] private bool _isTest;
    [ObservableProperty] private string _nowText = "";
    [ObservableProperty] private string _testBadge = "TEST";

    private double _total = 1;
    public string Kicker => (Core.AppInfo.DisplayName + " · power event").ToUpperInvariant();
    public string WindowTitle => Core.AppInfo.DisplayName + " · Shutdown";

    public void Update(ShutdownState st)
    {
        var active = st.Phase == ShutdownPhase.CountingDown;
        if (active && !IsActive) _total = Math.Max(1, st.Remaining(DateTimeOffset.UtcNow).TotalSeconds);
        IsActive = active;
        if (!active) return;
        var rem = st.Remaining(DateTimeOffset.UtcNow);
        if (rem.TotalSeconds > _total) _total = rem.TotalSeconds;   // after an extension
        TimeText = rem.TotalHours >= 1 ? rem.ToString(@"h\:mm\:ss") : $"{(int)rem.TotalMinutes:00}:{rem.Seconds:00}";
        Progress = Math.Clamp(rem.TotalSeconds / _total * 100, 0, 100);
        Reason = st.Reason ?? "";
        IsTest = _svc.TestMode;
        var act = _svc.Settings.Shutdown.Action;
        TestBadge = _svc.Power.PerformsRealActions ? "TEST · REAL " + act.ToString().ToUpperInvariant() : "TEST · SIMULATED";
        var a = _svc.Settings.Shutdown.Action;
        Heading = a switch { ShutdownAction.Hibernate => "Hibernating in", ShutdownAction.Sleep => "Going to sleep in", _ => "Shutting down in" };
        NowText = a switch { ShutdownAction.Hibernate => "Hibernate now", ShutdownAction.Sleep => "Sleep now", _ => "Shut down now" };
        CanExtend = _svc.Shutdown.CanExtend;
        ExtendText = "+" + ShutdownCoordinator.Fmt(_svc.Settings.Shutdown.ExtensionSeconds);
    }

    [RelayCommand] private Task Now() => _svc.ShutdownNowAsync();
    [RelayCommand] private void Extend() => _svc.Shutdown.Extend();
    [RelayCommand] private void Cancel() => _svc.CancelShutdown();
}
