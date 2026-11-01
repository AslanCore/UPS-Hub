using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpsHub.App.Services;
using UpsHub.Core;

namespace UpsHub.App.ViewModels;

/// <summary>Update state for the dashboard card and the Settings → About card.</summary>
public sealed partial class UpdateViewModel : ViewModelBase
{
    private readonly UpdateService _updates;
    private readonly UpsHubService _svc;
    private bool _loading;

    public UpdateViewModel(UpdateService updates, UpsHubService svc)
    {
        _updates = updates;
        _svc = svc;
        _loading = true;
        AutoCheck = svc.Settings.General.AutoCheckUpdates;
        _loading = false;
        updates.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Sync);
        Sync();
    }

    public UpdateService Service => _updates;

    [ObservableProperty] private UpdatePhase _phase;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _newVersion = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _published = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _autoCheck;
    [ObservableProperty] private bool _dismissed;

    public bool IsChecking => Phase == UpdatePhase.Checking;
    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;
    public bool IsUpToDate => Phase == UpdatePhase.UpToDate;
    public bool IsNoReleases => Phase == UpdatePhase.NoReleases;
    public bool IsAvailable => Phase == UpdatePhase.Available;
    public bool IsDownloading => Phase == UpdatePhase.Downloading;
    public bool IsReady => Phase is UpdatePhase.Ready or UpdatePhase.Installing;
    public bool IsFailed => Phase == UpdatePhase.Failed;
    public bool HasStatus => Phase != UpdatePhase.Idle || Message.Length > 0;
    public bool CanCheck => !IsBusy;
    public string CheckText => IsChecking ? "Checking…" : "Check for updates";
    public string InstallText => $"Install {NewVersion}";
    public string ProgressText => $"{Progress * 100:0}%";
    public string RepoUrl => AppInfo.RepoUrl;
    public string RepoLabel => AppInfo.RepoUrl.Replace("https://", "");
    public string LastCheckText => _updates.LastCheck is { } t ? "Last checked " + t.ToLocalTime().ToString("d MMM yyyy, HH:mm") : "Not checked yet";
    /// <summary>Dashboard card: an update was found (or is downloading / failed while installing) and not dismissed.</summary>
    public bool ShowCard => !Dismissed && _updates.Release is not null &&
        (Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Ready or UpdatePhase.Installing
         || Phase == UpdatePhase.Failed && _wasInstalling);
    private bool _wasInstalling;

    /// <summary>The kind of status line the About card shows: icon + colour by state.</summary>
    public string StatusKicker => Phase switch
    {
        UpdatePhase.Checking => "CHECKING",
        UpdatePhase.UpToDate => "UP TO DATE",
        UpdatePhase.NoReleases => "NO RELEASES PUBLISHED YET",
        UpdatePhase.Available => "UPDATE AVAILABLE",
        UpdatePhase.Downloading => "DOWNLOADING",
        UpdatePhase.Ready or UpdatePhase.Installing => "VERIFIED",
        UpdatePhase.Failed => "COULDN'T CHECK",
        _ => "",
    };

    private void Sync()
    {
        var prev = Phase;
        if (_updates.Phase is UpdatePhase.Downloading) _wasInstalling = true;
        else if (_updates.Phase is UpdatePhase.Available or UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.NoReleases) _wasInstalling = false;
        Phase = _updates.Phase;
        Message = _updates.Message;
        Progress = _updates.Progress;
        if (_updates.Release is { } r)
        {
            NewVersion = r.Version.ToString();
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? "No release notes." : CleanNotes(r.Notes);
            Published = r.Published is { } p ? "Released " + p.ToLocalTime().ToString("d MMM yyyy") : "";
        }
        if (prev != Phase && Phase == UpdatePhase.Available) Dismissed = false;
        foreach (var n in new[] { nameof(IsChecking), nameof(IsBusy), nameof(IsUpToDate), nameof(IsNoReleases), nameof(IsAvailable),
                     nameof(IsDownloading), nameof(IsReady), nameof(IsFailed), nameof(HasStatus), nameof(CanCheck), nameof(CheckText),
                     nameof(InstallText), nameof(ProgressText), nameof(ShowCard), nameof(StatusKicker), nameof(LastCheckText) })
            OnPropertyChanged(n);
    }

    /// <summary>GitHub markdown to plain text for the card: headings become caps lines, bullets become "•".</summary>
    public static string CleanNotes(string md)
    {
        var lines = md.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
        var outp = new List<string>();
        foreach (var l in lines)
        {
            var t = l.TrimStart();
            if (t.StartsWith('#')) { outp.Add(t.TrimStart('#', ' ').ToUpperInvariant()); continue; }
            if (t.StartsWith("- ") || t.StartsWith("* ")) { outp.Add("•  " + t[2..].Replace("**", "").Replace("`", "")); continue; }
            outp.Add(t.Replace("**", "").Replace("`", ""));
        }
        return string.Join("\n", outp).Trim();
    }

    partial void OnAutoCheckChanged(bool value)
    {
        if (_loading) return;
        _svc.Settings.General.AutoCheckUpdates = value;
        _svc.ApplySettings();
    }

    partial void OnDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowCard));

    [RelayCommand] private Task Check() => _updates.CheckAsync(manual: true);
    [RelayCommand] private Task Install() { Dismissed = false; return _updates.DownloadAndInstallAsync(); }
    [RelayCommand] private void CancelDownload() => _updates.CancelDownload();
    [RelayCommand] private void Skip() => _updates.Skip();
    [RelayCommand] private void Later() => Dismissed = true;
    [RelayCommand] private void ReleaseNotes() => _updates.OpenReleasePage();
    [RelayCommand]
    private void OpenRepo()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.RepoUrl) { UseShellExecute = true }); } catch { }
    }
}
