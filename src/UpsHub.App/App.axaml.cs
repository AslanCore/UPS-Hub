using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using UpsHub.App.Platform;
using UpsHub.App.Services;
using UpsHub.App.ViewModels;
using UpsHub.App.Views;
using UpsHub.Core.Settings;
using UpsHub.FakeNut;

namespace UpsHub.App;

public partial class App : Application
{
    private UpsHubService? _svc;
    private MainWindow? _window;
    private CountdownWindow? _countdown;
    private TrayService? _tray;
    private FakeNutServer? _demoServer;
    private MainViewModel? _vm;
    private UpdateService? _updates;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var opt = Program.Options;
            var exeDir = AppContext.BaseDirectory;
            var iconPath = Path.Combine(exeDir, "upshub.ico");
            var platform = PlatformServices.Create(dryRunPower: opt.Demo, iconPath: File.Exists(iconPath) ? iconPath : null);
            if (opt.Demo) platform.LocalUps = DemoLocalUps = new DemoLocalUps(platform.LocalUps);   // adds a simulated APC on USB

            // Demo / screenshot runs use a throwaway settings folder and a simulated UPS.
            var dir = opt.Demo
                ? Path.Combine(Path.GetTempPath(), "upshub-demo-" + Environment.ProcessId)
                : SettingsStore.DefaultDirectory();
            var store = new SettingsStore(dir);
            if (opt.Demo && opt.ScreenshotDir is null) SeedDemo(store);

            _svc = new UpsHubService(store, platform) { IsDemo = opt.Demo };
            if (opt.Demo)
            {
                _demoNet = new DemoNetwork();
                _svc.DiscoveryFactory = _demoNet.CreateDiscovery;
                _svc.DiscoveryTweak = _demoNet.Tweak;
            }
            _vm = new MainViewModel(_svc);
            // Updates: GitHub Releases for real; demo / screenshots use an in-process fake with a throwaway key.
            if (opt.Demo)
            {
                DemoGitHub = new DemoGitHub { Mode = opt.ScreenshotDir is null ? DemoReleaseMode.Available : DemoReleaseMode.NoReleases };
                _updates = new UpdateService(_svc, new HttpClient(DemoGitHub), DemoGitHub.Base, DemoGitHub.PublicKey)
                    { DryRunInstall = true, UpdatesDirectory = Path.Combine(dir, "updates") };
            }
            else _updates = new UpdateService(_svc);
            _updates.QuitForInstall = Quit;
            _vm.Updates = new UpdateViewModel(_updates, _svc);
            _window = new MainWindow { DataContext = _vm };
            _svc.InAppNotifier = _window;
            _countdown = new CountdownWindow { DataContext = _vm.Countdown };
            _vm.Countdown.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(CountdownViewModel.IsActive)) return;
                if (_vm.Countdown.IsActive) { _countdown.Show(); _countdown.Activate(); }
                else _countdown.Hide();
            };

            try
            {
                // The tray item only opens the confirmation on Shutdown rules; it never starts a test by itself.
                _tray = new TrayService(this, ShowMain, () => { ShowMain(); _vm.Navigate("shutdown"); _vm.Shutdown.TestCommand.Execute(null); }, Quit,
                    () => _vm.Service.ReconnectAll(all: true));
                _vm.TrayStateChanged += (h, t) => _tray.Update(h, t);
            }
            catch { _tray = null; /* no tray host (e.g. bare X session) */ }

            if (Program.Instance is { } inst) inst.ActivationRequested += () => Dispatcher.UIThread.Post(ShowMain);

            _svc.Start();
            var startHidden = !opt.Demo && (opt.Minimized || _svc.Settings.General.StartMinimized) && _tray is not null;
            if (!startHidden) _window.Show();
            else _vm.SetWindowActive(false);   // nothing on screen: no visual clock
            if (opt.ScreenshotDir is null) _updates.Start(TimeSpan.FromSeconds(opt.Demo ? 4 : 20));
            if (opt.Wizard) _vm.WizardImporter = new DemoWinNutImporter();
            if (opt.ScreenshotDir is null && (opt.Wizard || _svc.NeedsSetup))
            {
                _vm.OpenWizard();
                if (startHidden) ShowMain();   // first run: show the wizard even when told to start in the tray
            }

            if (opt.ScreenshotDir is { } shots)
                Dispatcher.UIThread.Post(async () =>
                {
                    if (opt.Phase4Shots) await ScreenshotRunner.RunPhase4Async(this, shots);
                    else if (opt.LocalShots) await ScreenshotRunner.RunLocalAsync(this, shots);
                    else if (opt.DiscoveryShots) await ScreenshotRunner.RunDiscoveryAsync(this, shots);
                    else await ScreenshotRunner.RunAsync(this, shots);
                }, DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void SeedDemo(SettingsStore store)
    {
        _demoServer = StartDemoServer();
        var s = new AppSettings { LegacyImportDone = true };
        s.Servers.Add(new ServerSettings { DisplayName = "Home NUT server (simulated)", Host = "127.0.0.1", Port = _demoServer.Port });
        s.General.StartWithWindows = false;
        store.Save(s);
    }

    internal FakeNutServer StartDemoServer()
    {
        _demoServer ??= new FakeNutServer()
            .Add(FakeUps.ApcBackUps("apc"))
            .Start();
        return _demoServer;
    }

    private DemoNetwork? _demoNet;
    internal DemoLocalUps? DemoLocalUps { get; private set; }
    internal DemoNetwork? DemoNet => _demoNet;
    internal DemoGitHub? DemoGitHub { get; private set; }
    internal UpdateService? Updates => _updates;
    internal UpsHubService Service => _svc!;
    internal MainViewModel ViewModel => _vm!;
    internal MainWindow MainWindowInstance => _window!;
    internal CountdownWindow CountdownWindowInstance => _countdown!;
    internal TrayService? Tray => _tray;

    public void ShowMain()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public async void Quit()
    {
        if (_window is not null) _window.AllowClose = true;
        _tray?.Dispose();
        _updates?.Dispose();
        if (_svc is not null) await _svc.DisposeAsync();
        if (_demoServer is not null) await _demoServer.DisposeAsync();
        if (_demoNet is not null) await _demoNet.DisposeAsync();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
