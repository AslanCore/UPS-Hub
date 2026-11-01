using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpsHub.App.Services;
using UpsHub.Core.Settings;

namespace UpsHub.App.ViewModels;

/// <summary>Edits <see cref="ShutdownRules"/> live; every change is saved and applied immediately.</summary>
public sealed partial class ShutdownViewModel : ViewModelBase
{
    private readonly UpsHubService _svc;
    private bool _loading;
    private ShutdownRules R => _svc.Settings.Shutdown;

    public ShutdownViewModel(UpsHubService svc)
    {
        _svc = svc;
        Load();
    }

    public ObservableCollection<string> UpsOptions { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _onLowBatteryFlag;
    [ObservableProperty] private decimal _batteryPercentBelow;
    [ObservableProperty] private decimal _runtimeSecondsBelow;
    [ObservableProperty] private decimal _onBatteryForSeconds;
    [ObservableProperty] private bool _followFsd;
    [ObservableProperty] private decimal _countdownSeconds;
    [ObservableProperty] private bool _allowExtension;
    [ObservableProperty] private decimal _extensionSeconds;
    [ObservableProperty] private bool _forceCloseApps;
    [ObservableProperty] private bool _actIfLost;
    [ObservableProperty] private ShutdownAction _action;
    [ObservableProperty] private string? _primaryUps;
    [ObservableProperty] private string _powerNote = "";

    public bool IsShutdown => Action == ShutdownAction.Shutdown;
    public bool IsHibernate => Action == ShutdownAction.Hibernate;
    public bool IsSleep => Action == ShutdownAction.Sleep;

    [RelayCommand] private void SetAction(string a) => Action = Enum.Parse<ShutdownAction>(a);
    // ---- Shutdown test: a real countdown ending in the real action, behind a confirmation ----
    [ObservableProperty] private bool _confirmingTest;
    [ObservableProperty] private string _testResult = "";
    public bool RealActions => PreviewRealActions ?? _svc.Power.PerformsRealActions;
    /// <summary>Screenshots only: show the wording a real Windows install gets. Power stays dry-run.</summary>
    internal bool? PreviewRealActions
    {
        get => _previewReal;
        set
        {
            _previewReal = value;
            if (value == true) PowerNote = "";
            foreach (var n in new[] { nameof(RealActions), nameof(TestDescription), nameof(TestWarning), nameof(StartTestText) }) OnPropertyChanged(n);
        }
    }
    private bool? _previewReal;
    public string ActionVerbUpper => ActionVerb.ToUpperInvariant();
    public string ActionVerb => Core.Shutdown.ShutdownController.ActionVerb(Action);
    public string TestDescription => RealActions
        ? $"Runs the real countdown. When it ends, this PC WILL really {ActionVerb}. Save your work first."
        : "Power actions are simulated here (demo mode or an unsupported system), so the test only shows the countdown.";
    public string TestWarning =>
        $"This is not a preview. When the countdown reaches zero, Windows will really {ActionVerb} this PC" +
        (Action == ShutdownAction.Shutdown && ForceCloseApps ? ", closing apps without asking" : "") +
        $". You get at least {Math.Max((int)CountdownSeconds, Core.Shutdown.ShutdownController.MinTestCountdownSeconds)} s, and Cancel in the countdown window stops it.";
    public string StartTestText => RealActions ? $"Start real {ActionName} test" : "Start simulated test";
    private string ActionName => Action switch { ShutdownAction.Hibernate => "hibernate", ShutdownAction.Sleep => "sleep", _ => "shutdown" };

    [RelayCommand]
    private void Test()
    {
        TestResult = "";
        if (RealActions) { ConfirmingTest = true; return; }
        StartTest();
    }

    [RelayCommand] private void CancelTest() => ConfirmingTest = false;

    [RelayCommand]
    private void StartTest()
    {
        ConfirmingTest = false;
        TestResult = _svc.StartShutdownTest() ? "" : "A countdown is already running.";
    }

    public void Load()
    {
        _loading = true;
        Enabled = R.Enabled; OnLowBatteryFlag = R.OnLowBatteryFlag; BatteryPercentBelow = R.BatteryPercentBelow;
        RuntimeSecondsBelow = R.RuntimeSecondsBelow; OnBatteryForSeconds = R.OnBatteryForSeconds; FollowFsd = R.FollowFsd;
        CountdownSeconds = R.CountdownSeconds; AllowExtension = R.AllowExtension; ExtensionSeconds = R.ExtensionSeconds;
        ForceCloseApps = R.ForceCloseApps; ActIfLost = R.ActIfLostWhileOnBattery; Action = R.Action;
        RefreshUpsOptions();
        PrimaryUps = R.PrimaryUps ?? UpsOptions.FirstOrDefault();
        PowerNote = _svc.Platform.Power.IsSupported ? "" : "Simulation only on this system: actions are logged, not performed.";
        _loading = false;
    }

    public void RefreshUpsOptions()
    {
        var keys = _svc.Snapshots.Keys.Select(k => k.ToString()).OrderBy(x => x).ToList();
        if (R.PrimaryUps is { } p && !keys.Contains(p)) keys.Insert(0, p);
        if (keys.SequenceEqual(UpsOptions)) return;
        var keep = PrimaryUps;
        UpsOptions.Clear();
        foreach (var k in keys) UpsOptions.Add(k);
        _loading = true;
        PrimaryUps = keep ?? UpsOptions.FirstOrDefault();
        _loading = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Action))
        {
            OnPropertyChanged(nameof(IsShutdown)); OnPropertyChanged(nameof(IsHibernate)); OnPropertyChanged(nameof(IsSleep));
        }
        if (e.PropertyName is nameof(Action) or nameof(CountdownSeconds) or nameof(ForceCloseApps))
        {
            OnPropertyChanged(nameof(ActionVerb)); OnPropertyChanged(nameof(ActionVerbUpper)); OnPropertyChanged(nameof(TestDescription));
            OnPropertyChanged(nameof(TestWarning)); OnPropertyChanged(nameof(StartTestText));
        }
        if (_loading || e.PropertyName is nameof(PowerNote) or nameof(IsShutdown) or nameof(IsHibernate) or nameof(IsSleep)
            or nameof(ConfirmingTest) or nameof(TestResult) or nameof(ActionVerb) or nameof(ActionVerbUpper) or nameof(RealActions) or nameof(TestDescription) or nameof(TestWarning) or nameof(StartTestText)) return;
        R.Enabled = Enabled; R.OnLowBatteryFlag = OnLowBatteryFlag; R.BatteryPercentBelow = (int)BatteryPercentBelow;
        R.RuntimeSecondsBelow = (int)RuntimeSecondsBelow; R.OnBatteryForSeconds = (int)OnBatteryForSeconds; R.FollowFsd = FollowFsd;
        R.CountdownSeconds = (int)CountdownSeconds; R.AllowExtension = AllowExtension; R.ExtensionSeconds = (int)ExtensionSeconds;
        R.ForceCloseApps = ForceCloseApps; R.ActIfLostWhileOnBattery = ActIfLost; R.Action = Action;
        R.PrimaryUps = PrimaryUps;
        _svc.ApplySettings();
    }
}
