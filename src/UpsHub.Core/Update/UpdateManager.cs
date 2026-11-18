namespace UpsHub.Core.Update;

public enum UpdatePhase { Idle, Checking, UpToDate, NoReleases, Available, Downloading, Ready, Installing, Failed }

/// <summary>Starts a verified installer. Completes with null once Setup is confirmed running, or with why it isn't.</summary>
public interface IInstallerLauncher
{
    Task<string?> StartAsync(string installerPath, string arguments, CancellationToken ct);
}

/// <summary>
/// The whole update lifecycle without UI: check both sources, download and verify, wait for a safe moment,
/// hand over to the silent installer, then ask the app to close so Setup can replace it and start it again.
/// Every step fails closed: anything unverified, older or unsafe leaves the running copy untouched.
/// </summary>
public sealed class UpdateManager
{
    private readonly UpdateChecker _checker;
    private readonly UpdateDownloader _downloader;
    private readonly UpdateStateStore _store;
    private readonly UpdateState _state;
    private readonly IInstallerLauncher _launcher;
    private readonly Func<InstallConditions> _conditions;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _cts;
    private DateTimeOffset _autoDownloadRetryAt = DateTimeOffset.MinValue;
    private int _busy;

    public static readonly TimeSpan DownloadRetryAfter = TimeSpan.FromHours(6);

    public UpdateManager(UpdateChecker checker, UpdateDownloader downloader, UpdateStateStore store, IInstallerLauncher launcher,
        Func<InstallConditions> conditions, SemVer current, string updatesDirectory, TimeProvider? time = null)
    {
        _checker = checker;
        _downloader = downloader;
        _store = store;
        _state = store.Load();
        _launcher = launcher;
        _conditions = conditions;
        _time = time ?? TimeProvider.System;
        Current = current;
        UpdatesDirectory = updatesDirectory;
        Message = _state.LastResult ?? "";
        if (AutoInstallPolicy.Reconcile(_state, current) is { } done)
        {
            Message = done;
            _store.Save(_state);
        }
    }

    public SemVer Current { get; }
    public string UpdatesDirectory { get; }
    /// <summary>Demo / screenshots: verify everything but never start an installer.</summary>
    public bool DryRun { get; init; }
    public Func<bool> AutoCheckEnabled { get; init; } = () => true;
    public Func<bool> AutoInstallEnabled { get; init; } = () => true;
    /// <summary>Relaunch into the tray (true) or with the window (false) after installing.</summary>
    public Func<bool> RelaunchToTray { get; init; } = () => true;

    public event Action? Changed;
    /// <summary>A newer release was found by an automatic check and will not be installed automatically.</summary>
    public event Action<VerifiedRelease>? FoundForUser;
    /// <summary>An automatic install is about to start (for a quiet notification).</summary>
    public event Action<VerifiedRelease>? AutoInstallStarting;
    /// <summary>Called once Setup is running; the app must close in an orderly way.</summary>
    public Action? QuitForInstall { get; set; }

    public UpdatePhase Phase { get; private set; }
    public VerifiedRelease? Release { get; private set; }
    public double Progress { get; private set; }
    public string Message { get; private set; }
    public string? InstallerPath { get; private set; }
    public DateTimeOffset? LastCheck => _state.LastCheckUtc;
    public UpdateState State => _state;
    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing || Volatile.Read(ref _busy) != 0;

    /// <summary>Fixed Inno Setup switches; nothing here comes from the network.</summary>
    public static string InstallerArguments(bool automatic, bool toTray, string? logPath)
    {
        // /NOCLOSEAPPLICATIONS: UPS Hub closes itself; Setup must not force-close anything (a file still in use
        // makes Setup fail, and it then restores the previous UpsHub.exe).
        var a = (automatic ? "/VERYSILENT" : "/SILENT") + " /SP- /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS " +
                (toTray ? "/RELAUNCH=tray" : "/RELAUNCH=1");
        if (logPath is not null && !logPath.Contains('"')) a += $" /LOG=\"{logPath}\"";
        return a;
    }

    /// <summary>Start-up and periodic: check when due, then install a verified newer version if allowed.</summary>
    public async Task AutoTickAsync()
    {
        if (!AutoCheckEnabled() || IsBusy) return;
        if (_checker.IsDue(_state)) await CheckAsync(manual: false);
        if (AutoInstallEnabled() && Release is { } r && r.Version > Current && Phase is UpdatePhase.Available or UpdatePhase.Ready or UpdatePhase.Failed)
            await InstallAsync(automatic: true);
    }

    public async Task CheckAsync(bool manual)
    {
        if (IsBusy || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            Set(UpdatePhase.Checking, "Checking AslanCore Cloud for a newer release…");
            var r = await _checker.CheckAsync(Current, _state, manual);
            _store.Save(_state);
            if (Release is { } old && (r.Release is null || old.Version != r.Release.Version || old.InstallerSha256 != r.Release.InstallerSha256)) InstallerPath = null;
            Release = r.Release;
            switch (r.Outcome)
            {
                case UpdateCheckOutcome.UpdateAvailable:
                    Set(UpdatePhase.Available, r.Message);
                    if (!manual && !AutoInstallEnabled()) FoundForUser?.Invoke(r.Release!);
                    break;
                case UpdateCheckOutcome.UpToDate: Set(UpdatePhase.UpToDate, r.Message); break;
                case UpdateCheckOutcome.Skipped: Set(UpdatePhase.Idle, r.Message); break;
                case UpdateCheckOutcome.NoReleases: Set(UpdatePhase.NoReleases, r.Message + " You're on the newest build."); break;
                default: Set(UpdatePhase.Failed, r.Message); break;
            }
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void CancelDownload() => _cts?.Cancel();

    /// <summary>Download (if needed), verify, wait for a safe moment and hand over to Setup.</summary>
    public async Task InstallAsync(bool automatic)
    {
        if (Release is not { } rel || IsBusy || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { await InstallCoreAsync(rel, automatic); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private async Task InstallCoreAsync(VerifiedRelease rel, bool automatic)
    {
        var now = _time.GetUtcNow();
        if (rel.Version <= Current) { Set(UpdatePhase.UpToDate, $"UPS Hub {Current} is up to date."); return; }
        if (Blocked() is { } why) { Set(UpdatePhase.Available, why); return; }
        if (automatic && !AutoInstallPolicy.MayAutoAttempt(_state, rel.Version, now))
        {
            if (_state.PendingAttempts >= AutoInstallPolicy.MaxAutoAttempts)
                Set(UpdatePhase.Available, $"Installing {rel.Version} automatically didn't work, so UPS Hub stopped retrying. Install it from Settings when convenient.");
            return;
        }

        if (InstallerPath is null || !File.Exists(InstallerPath))
        {
            if (automatic && now < _autoDownloadRetryAt) return;
            _cts = new CancellationTokenSource();
            Progress = 0;
            Set(UpdatePhase.Downloading, $"Downloading {rel.InstallerName}…");
            try
            {
                var progress = new Progress<double>(p => { Progress = p; Changed?.Invoke(); });
                InstallerPath = await _downloader.DownloadAsync(rel, UpdatesDirectory, progress, _cts.Token);
            }
            catch (OperationCanceledException) { Set(UpdatePhase.Available, "Download cancelled."); return; }
            catch (UpdateVerificationException ex) { DownloadFailed(ex.Message + " The update was not installed."); return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            { DownloadFailed("Download failed: " + ex.Message); return; }
        }
        Set(UpdatePhase.Ready, "Signature and SHA-256 verified.");

        // The file sits in a per-user folder; check it once more right before it runs.
        if (!await UpdateDownloader.StillMatchesAsync(InstallerPath!, rel))
        {
            try { File.Delete(InstallerPath!); } catch { }
            InstallerPath = null;
            Set(UpdatePhase.Failed, "The downloaded installer no longer matches the release and was deleted. The update was not installed.");
            return;
        }
        if (Blocked() is { } later) { Set(UpdatePhase.Ready, later); return; }

        var args = InstallerArguments(automatic, RelaunchToTray(), Path.Combine(UpdatesDirectory, "install.log"));
        if (DryRun) { Set(UpdatePhase.Ready, $"Verified. Demo mode: would now run {rel.InstallerName} {args} and restart."); return; }

        AutoInstallPolicy.RecordAttempt(_state, rel.Version, now);
        _store.Save(_state);
        if (automatic) AutoInstallStarting?.Invoke(rel);
        Set(UpdatePhase.Installing, "Installing… UPS Hub restarts when it's done.");
        string? err;
        try { err = await _launcher.StartAsync(InstallerPath!, args, CancellationToken.None); }
        catch (Exception ex) { err = ex.Message; }
        if (err is not null)
        {
            Set(UpdatePhase.Failed, $"Couldn't start the installer: {err} UPS Hub keeps running {Current}.");
            return;
        }
        // Setup is running and waits (up to a minute) for UPS Hub to close. If a UPS went on battery or a
        // shutdown started meanwhile, stay up: Setup then gives up on its own without changing anything.
        if (Blocked() is { } now2)
        {
            AutoInstallPolicy.UndoAttempt(_state, rel.Version);
            _store.Save(_state);
            Set(UpdatePhase.Ready, "Install postponed. " + now2);
            return;
        }
        QuitForInstall?.Invoke();
    }

    private string? Blocked()
    {
        try { return AutoInstallPolicy.BlockReason(_conditions()); }
        catch (Exception) { return "Waiting to install: UPS Hub couldn't confirm it's safe to close right now."; }
    }

    private void DownloadFailed(string msg)
    {
        _autoDownloadRetryAt = _time.GetUtcNow() + DownloadRetryAfter;
        InstallerPath = null;
        Set(UpdatePhase.Failed, msg);
    }

    public void Skip()
    {
        if (Release is null) return;
        _state.SkippedVersion = Release.Version.ToString();
        _store.Save(_state);
        Set(UpdatePhase.Idle, $"Version {Release.Version} skipped. Check manually from Settings any time.");
    }

    public void Dismiss() { if (Phase is UpdatePhase.Available or UpdatePhase.Failed or UpdatePhase.Ready) Set(UpdatePhase.Idle, Message); }

    public void CleanOldDownloads()
    {
        try
        {
            if (!Directory.Exists(UpdatesDirectory)) return;
            foreach (var f in Directory.GetFiles(UpdatesDirectory))
                if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-2)) File.Delete(f);
        }
        catch { }
    }

    private void Set(UpdatePhase p, string msg)
    {
        Phase = p; Message = msg;
        Changed?.Invoke();
    }
}
