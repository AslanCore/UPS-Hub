using System.Diagnostics;
using Avalonia.Threading;
using UpsHub.Core;
using UpsHub.Core.Update;

namespace UpsHub.App.Services;

public enum UpdatePhase { Idle, Checking, UpToDate, NoReleases, Available, Downloading, Ready, Installing, Failed }

/// <summary>
/// Checks GitHub Releases (on start-up when a day has passed, then hourly re-evaluation, or on demand),
/// downloads and verifies the installer (signature + SHA-256), then runs it silently and quits so it can
/// replace the files. The installer relaunches UPS Hub when done.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly UpsHubService _svc;
    private readonly UpdateChecker _checker;
    private readonly UpdateDownloader _downloader;
    private readonly UpdateStateStore _store;
    private readonly UpdateState _state;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;

    public event Action? Changed;
    /// <summary>Asked to quit the app once the installer has started.</summary>
    public Action? QuitForInstall { get; set; }
    /// <summary>Demo / screenshots: verify everything but don't run the installer.</summary>
    public bool DryRunInstall { get; init; }
    public string UpdatesDirectory { get; init; }

    public UpdatePhase Phase { get; private set; }
    public ReleaseInfo? Release { get; private set; }
    public double Progress { get; private set; }
    public string Message { get; private set; } = "";
    public string? InstallerPath { get; private set; }
    public DateTimeOffset? LastCheck => _state.LastCheckUtc;
    public bool CanInstallHere => OperatingSystem.IsWindows() || DryRunInstall;
    public static SemVer Current => SemVer.TryParse(AppInfo.Version) ?? new SemVer(0, 0, 0, Array.Empty<string>());

    public UpdateService(UpsHubService svc, HttpClient? http = null, Uri? apiBase = null, Minisign.PublicKey? key = null, string? repo = null)
    {
        _svc = svc;
        _http = http ?? UpdateChecker.CreateHttpClient(AppInfo.Version);
        _checker = new UpdateChecker(_http, repo ?? UpdateChecker.RepoFromUrl(AppInfo.RepoUrl) ?? "cyberwill01/UPS-Hub", apiBase);
        _downloader = new UpdateDownloader(_http, key ?? ReleaseKey.Public);
        _store = new UpdateStateStore(svc.Store.Directory);
        _state = _store.Load();
        UpdatesDirectory = Path.Combine(LocalDataDirectory(), "updates");
        Message = _state.LastResult ?? "";
        _timer = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => _ = AutoCheckAsync());
    }

    public static string LocalDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.SettingsFolderName);

    /// <summary>First automatic check shortly after start-up, then re-evaluated hourly (checks at most once a day).</summary>
    public void Start(TimeSpan firstDelay)
    {
        CleanOldDownloads();
        DispatcherTimer.RunOnce(() => _ = AutoCheckAsync(), firstDelay);
        _timer.Start();
    }

    private async Task AutoCheckAsync()
    {
        if (!_svc.Settings.General.AutoCheckUpdates || Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing) return;
        if (!_checker.IsDue(_state)) return;
        await CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        if (Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing) return;
        Set(UpdatePhase.Checking, "Checking GitHub for a newer release…");
        var r = await _checker.CheckAsync(Current, _state, manual);
        _store.Save(_state);
        Release = r.Release;
        switch (r.Outcome)
        {
            case UpdateCheckOutcome.UpdateAvailable:
                Set(UpdatePhase.Available, r.Message);
                if (!manual && _svc.Settings.General.Notifications)
                    _svc.Notify("Update available", $"{AppInfo.DisplayName} {r.Release!.Version} is ready to install from the dashboard.", Core.Platform.NotificationKind.Info);
                break;
            case UpdateCheckOutcome.UpToDate: Set(UpdatePhase.UpToDate, r.Message); break;
            case UpdateCheckOutcome.Skipped: Set(UpdatePhase.Idle, r.Message); break;
            case UpdateCheckOutcome.NoReleases: Set(UpdatePhase.NoReleases, r.Message + " You're on the newest build."); break;
            default: Set(UpdatePhase.Failed, r.Message); break;
        }
    }

    public async Task DownloadAndInstallAsync()
    {
        if (Release is not { } rel || Phase is UpdatePhase.Downloading or UpdatePhase.Installing) return;
        if (!CanInstallHere) { Set(UpdatePhase.Failed, "Automatic install is for the Windows build. Download the release from GitHub instead."); return; }
        _cts = new CancellationTokenSource();
        Progress = 0;
        Set(UpdatePhase.Downloading, $"Downloading {rel.Installer?.Name ?? "installer"}…");
        try
        {
            var progress = new Progress<double>(p => { Progress = p; Changed?.Invoke(); });
            InstallerPath = await _downloader.DownloadAsync(rel, UpdatesDirectory, progress, _cts.Token);
            Set(UpdatePhase.Ready, "Signature and SHA-256 verified.");
            await Task.Delay(600);
            Install();
        }
        catch (OperationCanceledException) { Set(UpdatePhase.Available, "Download cancelled."); }
        catch (UpdateVerificationException ex) { Set(UpdatePhase.Failed, ex.Message + " The update was not installed."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        { Set(UpdatePhase.Failed, "Download failed: " + ex.Message); }
    }

    public void CancelDownload() => _cts?.Cancel();

    /// <summary>Inno Setup silent upgrade. /RELAUNCH=1 makes the installer start UPS Hub again when it's done.</summary>
    public static string InstallerArguments => "/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /RELAUNCH=1";

    private void Install()
    {
        if (InstallerPath is null) return;
        if (DryRunInstall)
        {
            Set(UpdatePhase.Ready, $"Verified. Demo mode: would now run {Path.GetFileName(InstallerPath)} {InstallerArguments} and restart.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(InstallerPath, InstallerArguments) { UseShellExecute = true, WorkingDirectory = UpdatesDirectory });
            Set(UpdatePhase.Installing, "Installing… UPS Hub restarts when it's done.");
            DispatcherTimer.RunOnce(() => QuitForInstall?.Invoke(), TimeSpan.FromMilliseconds(400));
        }
        catch (Exception ex) { Set(UpdatePhase.Failed, "Couldn't start the installer: " + ex.Message); }
    }

    public void Skip()
    {
        if (Release is null) return;
        _state.SkippedVersion = Release.Version.ToString();
        _store.Save(_state);
        Set(UpdatePhase.Idle, $"Version {Release.Version} skipped. Check manually from Settings any time.");
    }

    public void Dismiss() { if (Phase is UpdatePhase.Available or UpdatePhase.Failed or UpdatePhase.Ready) Set(UpdatePhase.Idle, Message); }

    public void OpenReleasePage()
    {
        var url = Release?.HtmlUrl?.ToString() ?? (AppInfo.RepoUrl + "/releases");
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void CleanOldDownloads()
    {
        try
        {
            if (!Directory.Exists(UpdatesDirectory)) return;
            foreach (var f in Directory.GetFiles(UpdatesDirectory))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-2)) File.Delete(f);
        }
        catch { }
    }

    private void Set(UpdatePhase p, string msg)
    {
        Phase = p; Message = msg;
        Changed?.Invoke();
    }

    public void Dispose() { _timer.Stop(); _cts?.Cancel(); }
}
