using System.Diagnostics;
using Avalonia.Threading;
using UpsHub.Core;
using UpsHub.Core.Update;

namespace UpsHub.App.Services;

/// <summary>
/// App side of updates: timers, settings and notifications around <see cref="UpdateManager"/>.
/// Checks AslanCore Cloud and public GitHub releases (shortly after start-up when a day has passed, re-evaluated
/// every 15 minutes, or on demand), takes the newest release the release key vouches for, downloads and verifies
/// it, and, when every UPS is on mains power, runs Setup silently and closes so it can be replaced. Setup starts
/// UPS Hub again afterwards, also when it fails.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly UpsHubService _svc;
    private readonly UpdateManager _mgr;
    private readonly DispatcherTimer _timer;

    public event Action? Changed;
    /// <summary>Asked to quit the app once the installer has started.</summary>
    public Action? QuitForInstall { get; set; }
    /// <summary>Is the main window hidden (tray only)? Setup then restarts UPS Hub in the tray, not with its window.</summary>
    public Func<bool>? WindowHidden { get; set; }

    public UpdatePhase Phase => _mgr.Phase;
    public VerifiedRelease? Release => _mgr.Release;
    public double Progress => _mgr.Progress;
    public string Message => _mgr.Message;
    public DateTimeOffset? LastCheck => _mgr.LastCheck;
    public static SemVer Current => SemVer.TryParse(AppInfo.Version) ?? new SemVer(0, 0, 0, Array.Empty<string>());
    internal UpdateManager Manager => _mgr;

    public UpdateService(UpsHubService svc, HttpClient? http = null, UpdateFeedOptions? options = null, bool dryRun = false,
        string? updatesDirectory = null, IInstallerLauncher? launcher = null, Func<Core.Update.InstallConditions>? conditions = null)
    {
        _svc = svc;
        http ??= UpdateChecker.CreateHttpClient(AppInfo.Version);
        options ??= new UpdateFeedOptions { GitHubRepo = UpdateChecker.RepoFromUrl(AppInfo.RepoUrl) ?? "AslanCore/UPS-Hub" };
        var scope = InstallScopeDetector.Detect(AppContext.BaseDirectory);
        _mgr = new UpdateManager(new UpdateChecker(http, options), new UpdateDownloader(http, options), new UpdateStateStore(svc.Store.Directory),
            launcher ?? new ProcessInstallerLauncher(), conditions ?? (() => Conditions(scope)), Current,
            updatesDirectory ?? Path.Combine(LocalDataDirectory(), "updates"))
        {
            DryRun = dryRun,
            AutoCheckEnabled = () => _svc.Settings.General.AutoCheckUpdates,
            AutoInstallEnabled = () => _svc.Settings.General.AutoCheckUpdates && _svc.Settings.General.AutoInstallUpdates,
            RelaunchToTray = () => WindowHidden?.Invoke() ?? true,
        };
        _mgr.Changed += () => Changed?.Invoke();
        _mgr.QuitForInstall = () => DispatcherTimer.RunOnce(() => QuitForInstall?.Invoke(), TimeSpan.FromMilliseconds(100));
        _mgr.FoundForUser += r =>
        {
            if (_svc.Settings.General.Notifications)
                _svc.Notify("Update available", $"{AppInfo.DisplayName} {r.Version} is ready to install from the dashboard.", Core.Platform.NotificationKind.Info);
        };
        _mgr.AutoInstallStarting += r =>
        {
            if (_svc.Settings.General.Notifications)
                _svc.Notify("Updating UPS Hub", $"{AppInfo.DisplayName} {r.Version} is installing and starts again in a moment.", Core.Platform.NotificationKind.Info);
        };
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(15), DispatcherPriority.Background, (_, _) => _ = _mgr.AutoTickAsync());
    }

    public static string LocalDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.SettingsFolderName);

    /// <summary>Last known state of every UPS plus the shutdown state, for the install gate.</summary>
    private Core.Update.InstallConditions Conditions(InstallScopeInfo scope) => new(
        OperatingSystem.IsWindows(), scope.Scope, scope.Writable,
        _svc.Shutdown.State.Phase, _svc.Power.IsRunningAction, _svc.TestMode,
        _svc.Snapshots.Values.Select(s => s.Status).ToList());

    /// <summary>First automatic check shortly after start-up, then re-evaluated every 15 minutes (checks at most once a day).</summary>
    public void Start(TimeSpan firstDelay)
    {
        _mgr.CleanOldDownloads();
        DispatcherTimer.RunOnce(() => _ = _mgr.AutoTickAsync(), firstDelay);
        _timer.Start();
    }

    public Task CheckAsync(bool manual) => _mgr.CheckAsync(manual);
    public Task DownloadAndInstallAsync() => _mgr.InstallAsync(automatic: false);
    public void CancelDownload() => _mgr.CancelDownload();
    public void Skip() => _mgr.Skip();
    public void Dismiss() => _mgr.Dismiss();
    public void OpenReleasePage()
    {
        var url = Release?.HtmlUrl?.ToString() ?? (AppInfo.RepoUrl + "/releases");
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public void Dispose() { _timer.Stop(); _mgr.CancelDownload(); }
}

public sealed record InstallScopeInfo(InstallScope Scope, bool Writable);

internal static class InstallScopeDetector
{
    public const string SetupAppId = "{6F1C2B7E-4E53-4C8B-9A3F-2D5B7A1E0C44}";

    public static InstallScopeInfo Detect(string exeDir)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return Windows(exeDir);
#endif
        return new(InstallScope.Other, false);
    }

#if WINDOWS
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static InstallScopeInfo Windows(string exeDir)
    {
        try
        {
            if (!File.Exists(Path.Combine(exeDir, "unins000.exe"))) return new(InstallScope.Portable, false);
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{SetupAppId}_is1");
            var loc = key?.GetValue("InstallLocation") as string;
            if (loc is null || !SamePath(loc, exeDir)) return new(InstallScope.Other, false);
            return new(InstallScope.PerUser, Writable(exeDir));
        }
        catch { return new(InstallScope.Other, false); }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool Writable(string dir)
    {
        try
        {
            using var f = new FileStream(Path.Combine(dir, $".upshub-write-test-{Guid.NewGuid():n}.tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch { return false; }
    }
#endif
}

/// <summary>
/// Starts Setup without the shell (so Windows never shows an elevation prompt: an installer that wanted admin
/// rights would simply fail to start) and confirms that this very process is the Setup now running: its
/// mutex must not exist before the start, must appear afterwards, and the started process must still be alive.
/// </summary>
internal sealed class ProcessInstallerLauncher : IInstallerLauncher
{
    public const string SetupMutex = "AslanCore.UpsHub.Setup";
    public Func<bool> SetupRunning { get; init; } = DefaultProbe;
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ConfirmDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    private static bool DefaultProbe()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!Mutex.TryOpenExisting(SetupMutex, out var m)) return false;
        m.Dispose();
        return true;
    }

    public async Task<string?> StartAsync(string installerPath, string arguments, CancellationToken ct)
    {
        if (SetupRunning()) return "another UPS Hub Setup is already running.";
        Process? p;
        try { p = Process.Start(new ProcessStartInfo(installerPath, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installerPath)! }); }
        catch (Exception ex) { return ex.Message; }
        if (p is null) return "Windows didn't start it.";
        using (p)
        {
            var until = DateTime.UtcNow + StartTimeout;
            while (DateTime.UtcNow < until)
            {
                if (p.HasExited) return $"it closed straight away (exit code {p.ExitCode}).";
                if (SetupRunning())
                {
                    await Task.Delay(ConfirmDelay, ct);
                    if (!p.HasExited) return null;
                    return $"it closed straight away (exit code {p.ExitCode}).";
                }
                await Task.Delay(250, ct);
            }
            try { p.Kill(entireProcessTree: true); } catch { }
            return "it didn't start within a minute, so it was stopped.";
        }
    }
}
