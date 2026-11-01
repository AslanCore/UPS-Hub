using Avalonia.VisualTree;
using UpsHub.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using UpsHub.Core.Settings;
using UpsHub.Core.Shutdown;
using UpsHub.FakeNut;

namespace UpsHub.App.Services;

/// <summary>
/// `--screenshots DIR`: drives the real UI through every screen against the fake NUT server,
/// including an OL -> OB -> LB outage, and saves PNGs. Used for visual QA; power actions are dry-run.
/// </summary>
internal static class ScreenshotRunner
{
    public static async Task RunAsync(App app, string dir)
    {
        Directory.CreateDirectory(dir);
        var w = app.MainWindowInstance;
        var vm = app.ViewModel;
        var svc = app.Service;
        try
        {
            await Task.Delay(1200);
            Save(w, dir, "00-dashboard-empty");

            // Connect to the simulated APC on the fake upsd
            var fake = app.StartDemoServer();
            svc.Settings.Servers.Add(new ServerSettings { DisplayName = "Home NUT server (simulated)", Host = "127.0.0.1", Port = fake.Port });
            svc.Settings.General.PollIntervalSeconds = 1;
            svc.Save();
            svc.RebuildMonitors();
            var ups = fake.Ups["apc"];
            for (int i = 0; i < 25; i++)
            {
                ups.Set("ups.load", 21 + (i % 5)).Set("input.voltage", 229.5 + (i % 3));
                await Task.Delay(200);
            }
            Save(w, dir, "01-dashboard-online");

            vm.Navigate("servers");
            await Task.Delay(700);
            vm.Servers.Host = "192.0.2.20";
            vm.Servers.DisplayName = "NAS";
            vm.Servers.Username = "upsmon";
            await Task.Delay(300);
            Save(w, dir, "02-servers");

            vm.Navigate("shutdown");
            await Task.Delay(700);
            Save(w, dir, "03-shutdown-rules");

            vm.Navigate("settings");
            await Task.Delay(700);
            Save(w, dir, "04-settings");

            // Outage: OL -> OB
            vm.Navigate("dashboard");
            svc.Settings.Shutdown.CountdownSeconds = 45;
            svc.ApplySettings();
            for (double t = 0; t <= 14; t += 1)
            {
                OutageScenario.Apply(ups, t);
                await Task.Delay(220);
            }
            await Task.Delay(1500);
            Save(w, dir, "05-dashboard-on-battery");

            // OB -> LB: the countdown starts
            for (double t = 15; t <= 34; t += 1)
            {
                OutageScenario.Apply(ups, t);
                await Task.Delay(150);
            }
            for (int i = 0; i < 30 && svc.Shutdown.State.Phase != ShutdownPhase.CountingDown; i++) await Task.Delay(200);
            await Task.Delay(2600);
            Save(w, dir, "06-dashboard-low-battery");
            Save(app.CountdownWindowInstance, dir, "07-countdown");

            // power back -> countdown cancels
            OutageScenario.Apply(ups, -1);
            await Task.Delay(2500);
            Save(w, dir, "08-power-restored");
            app.Tray?.SaveIcons(dir);
            File.WriteAllText(Path.Combine(dir, "run.txt"),
                $"phase={svc.Shutdown.State.Phase} lastCancel={svc.Shutdown.State.LastCancelReason} action={svc.LastActionResult}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
        }
        finally
        {
            app.Quit();
        }
    }

    /// <summary>`--screenshots-discovery DIR`: first-run prompt, scan in progress, results, login, and the nothing-found help.</summary>
    public static async Task RunDiscoveryAsync(App app, string dir)
    {
        Directory.CreateDirectory(dir);
        var w = app.MainWindowInstance;
        var vm = app.ViewModel;
        var svc = app.Service;
        var net = app.DemoNet!;
        try
        {
            w.Width = 1180; w.Height = 860;
            await Task.Delay(1400);
            Save(w, dir, "p2-00-first-run");

            // One server already configured, so discovery can show the "Added" state for it.
            var fake = app.StartDemoServer();
            svc.Settings.Servers.Add(new ServerSettings { DisplayName = "This PC", Host = "127.0.0.1", Port = fake.Port });
            svc.Save();
            svc.RebuildMonitors();

            vm.Navigate("servers");
            await Task.Delay(500);
            var d = vm.Servers.Discovery;
            var scan = d.StartAsync();
            await Task.Delay(1900);
            Save(w, dir, "p2-01-scanning");
            await scan;
            await Task.Delay(600);
            Save(w, dir, "p2-02-results");

            var nas = d.Results.First(r => r.AuthRequired);
            await nas.AddCommand.ExecuteAsync(null);
            nas.Username = DemoNetwork.NasUser;
            nas.Password = DemoNetwork.NasPassword;
            await Task.Delay(400);
            Save(w, dir, "p2-03-login");
            await nas.AddCommand.ExecuteAsync(null);
            var rack = d.Results.First(r => r.Model.TlsSupported == true);
            await rack.AddCommand.ExecuteAsync(null);
            d.ShowOptions = true;
            await Task.Delay(2500);
            Save(w, dir, "p2-04-added-options");

            d.ShowOptions = false;
            net.Empty = true;
            await d.StartAsync();
            await Task.Delay(600);
            Save(w, dir, "p2-05-nothing-found");

            vm.Navigate("dashboard");
            await Task.Delay(1500);
            Save(w, dir, "p2-06-dashboard-multi-ups");
            File.WriteAllText(Path.Combine(dir, "run.txt"),
                $"servers={string.Join(", ", svc.Settings.Servers.Select(x => $"{x.Host}:{x.Port} user={x.Username ?? "-"} tls={x.Tls}"))}\n" +
                $"known={string.Join(", ", svc.Settings.Discovery.KnownHosts)}\n" +
                string.Join("\n", svc.Settings.Servers.Select(x => $"{x.Host}:{x.Port} -> {svc.StatusOf(x.Id)?.State}")) + "\n" +
                "snapshots=" + string.Join(", ", svc.Snapshots.Keys) + "\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
        }
        finally
        {
            app.Quit();
        }
    }

    /// <summary>`--screenshots-local DIR`: the Local USB UPS section, adding the simulated APC, its dashboard, an outage and an unplug.</summary>
    public static async Task RunLocalAsync(App app, string dir)
    {
        Directory.CreateDirectory(dir);
        var w = app.MainWindowInstance;
        var vm = app.ViewModel;
        var svc = app.Service;
        var demo = app.DemoLocalUps!;
        var log = new System.Text.StringBuilder();
        try
        {
            w.Width = 1180; w.Height = 860;
            svc.Settings.Discovery.FirstRunPromptDone = true;
            svc.Settings.General.PollIntervalSeconds = 1;
            svc.Save();
            await Task.Delay(1200);

            vm.Navigate("servers");
            await Task.Delay(500);
            Save(w, dir, "p3-00-servers-local-section");
            await vm.Servers.Local.ScanAsync();
            await Task.Delay(500);
            Save(w, dir, "p3-01-local-found");
            log.AppendLine("found=" + string.Join(" | ", vm.Servers.Local.Devices.Select(d => $"{d.Title} [{d.Tier}] {d.Meta}")));

            vm.Servers.Local.Devices.First(d => d.Info.Tier == Core.Settings.LocalTier.Replay).AddCommand.Execute(null);
            await Task.Delay(2600);
            ScrollTo(w, 330);
            await Task.Delay(300);
            Save(w, dir, "p3-02-local-added");

            vm.Navigate("dashboard");
            await Task.Delay(3500);
            Save(w, dir, "p3-03-dashboard-usb");
            log.AppendLine("kicker=" + vm.Dashboard.Kicker + " subtitle=" + vm.Dashboard.Subtitle + " pill=" + vm.Dashboard.PillText);

            // Mains lost: the APC switches to battery and discharges.
            double charge = 100;
            demo.Script = (d, t) =>
            {
                charge = Math.Max(0, charge - 2.5);
                d.Apply(acPresent: false, charging: false, charge: charge, runtime: Math.Round(2257 * charge / 100), load: 24, inputVoltage: 0,
                        lowBattery: charge <= 10);
            };
            await Task.Delay(4500);
            Save(w, dir, "p3-04-dashboard-usb-on-battery");
            log.AppendLine("onbattery pill=" + vm.Dashboard.PillText + " status=" + svc.PrimarySnapshot?.Status);

            demo.Script = null;
            await Task.Delay(2500);
            // USB cable pulled: data goes stale, the monitor keeps retrying.
            demo.Unplugged = true;
            await Task.Delay(3500);
            Save(w, dir, "p3-05-dashboard-usb-unplugged");
            log.AppendLine("unplugged title=" + vm.Dashboard.Title + " subtitle=" + vm.Dashboard.Subtitle);
            vm.Navigate("servers");
            await Task.Delay(700);
            ScrollTo(w, 330);
            await Task.Delay(300);
            Save(w, dir, "p3-06-servers-usb-retrying");
            demo.Unplugged = false;
            await Task.Delay(4500);
            vm.Navigate("dashboard");
            await Task.Delay(1500);
            Save(w, dir, "p3-07-dashboard-usb-reconnected");
            log.AppendLine("replugged title=" + vm.Dashboard.Title + " connected=" + svc.Monitors.First().Status.State);
            File.WriteAllText(Path.Combine(dir, "run.txt"), log.ToString());
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), log + "\n" + ex);
        }
        finally
        {
            app.Quit();
        }
    }

    private static void ScrollTo(Window w, double y)
    {
        foreach (var sv in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
            sv.Offset = new Vector(0, y);
    }

    /// <summary>`--screenshots-phase4 DIR`: About card update states, update card, shutdown test confirmation, setup wizard.</summary>
    public static async Task RunPhase4Async(App app, string dir)
    {
        Directory.CreateDirectory(dir);
        var w = app.MainWindowInstance;
        var vm = app.ViewModel;
        var svc = app.Service;
        var gh = app.DemoGitHub!;
        var upd = vm.Updates!;
        try
        {
            await Task.Delay(800);
            var fake = app.StartDemoServer();
            svc.Settings.Servers.Add(new ServerSettings { DisplayName = "Home NUT server (simulated)", Host = "127.0.0.1", Port = fake.Port });
            svc.Settings.General.PollIntervalSeconds = 2;
            svc.Settings.SetupCompleted = true;
            svc.Save();
            svc.RebuildMonitors();
            await Task.Delay(3000);

            // ---- Settings → About: every inline update state ----
            vm.Navigate("settings");
            await Task.Delay(900);
            void ScrollEnd()
            {
                foreach (var sv in w.GetVisualDescendants().OfType<ScrollViewer>()) sv.ScrollToEnd();
            }
            ScrollEnd(); await Task.Delay(400);
            Save(w, dir, "p4-01-about-idle");

            gh.Mode = DemoReleaseMode.NoReleases;
            gh.Latency = TimeSpan.FromMilliseconds(2500);
            var check = upd.CheckCommand.ExecuteAsync(null);
            await Task.Delay(700); ScrollEnd(); await Task.Delay(300);
            Save(w, dir, "p4-02-about-checking");
            await check; await Task.Delay(500); ScrollEnd(); await Task.Delay(300);
            Save(w, dir, "p4-03-about-no-releases");
            gh.Latency = TimeSpan.FromMilliseconds(200);

            gh.Mode = DemoReleaseMode.UpToDate;
            await upd.CheckCommand.ExecuteAsync(null); await Task.Delay(500); ScrollEnd(); await Task.Delay(300);
            Save(w, dir, "p4-04-about-up-to-date");

            gh.Mode = DemoReleaseMode.Error;
            await upd.CheckCommand.ExecuteAsync(null); await Task.Delay(500); ScrollEnd(); await Task.Delay(300);
            Save(w, dir, "p4-05-about-error");

            gh.Mode = DemoReleaseMode.Available;
            await upd.CheckCommand.ExecuteAsync(null); await Task.Delay(500); ScrollEnd(); await Task.Delay(300);
            Save(w, dir, "p4-06-about-update-available");

            // ---- Dashboard update card ----
            vm.Navigate("dashboard");
            await Task.Delay(1200);
            Save(w, dir, "p4-07-dashboard-update-card");
            gh.Latency = TimeSpan.FromMilliseconds(1500);
            var install = upd.InstallCommand.ExecuteAsync(null);
            await Task.Delay(400);
            Save(w, dir, "p4-08-dashboard-update-downloading");
            await install; await Task.Delay(400);
            Save(w, dir, "p4-09-dashboard-update-verified");
            upd.LaterCommand.Execute(null);
            gh.Latency = TimeSpan.FromMilliseconds(200);

            // ---- Shutdown rules: real-test confirmation (wording of a real Windows install; power stays dry-run) ----
            vm.Navigate("shutdown");
            await Task.Delay(900);
            vm.Shutdown.PreviewRealActions = true;
            vm.Shutdown.Action = ShutdownAction.Shutdown;
            foreach (var sv in w.GetVisualDescendants().OfType<ScrollViewer>()) sv.ScrollToEnd();
            await Task.Delay(400);
            Save(w, dir, "p4-10-shutdown-test-button");
            vm.Shutdown.TestCommand.Execute(null);
            await Task.Delay(500);
            Save(w, dir, "p4-11-shutdown-test-confirm");
            vm.Shutdown.Action = ShutdownAction.Hibernate;
            await Task.Delay(400);
            Save(w, dir, "p4-12-shutdown-test-confirm-hibernate");
            vm.Shutdown.Action = ShutdownAction.Shutdown;
            vm.Shutdown.PreviewRealActions = null;
            vm.Shutdown.CancelTestCommand.Execute(null);
            // simulated test in demo: countdown window shows TEST · SIMULATED
            vm.Shutdown.TestCommand.Execute(null);
            await Task.Delay(1500);
            Save(app.CountdownWindowInstance, dir, "p4-13-countdown-test-simulated");
            svc.CancelShutdown();
            await Task.Delay(600);
            w.ClearToasts();
            await Task.Delay(700);

            // ---- Setup wizard ----
            vm.WizardImporter = new Platform.DemoWinNutImporter();
            vm.OpenWizard();
            var wz = vm.Wizard!;
            await Task.Delay(900);
            Save(w, dir, "p4-20-wizard-welcome");
            wz.BeginCommand.Execute(null);
            await Task.Delay(1500);
            Save(w, dir, "p4-21-wizard-connect-scanning");
            for (int i = 0; i < 120 && wz.Discovery.IsScanning; i++) await Task.Delay(250);
            await Task.Delay(600);
            Save(w, dir, "p4-22-wizard-connect-auto-results");
            wz.Mode = ConnectMode.Usb;
            for (int i = 0; i < 40 && !wz.Local.Scanned; i++) await Task.Delay(200);
            await Task.Delay(500);
            Save(w, dir, "p4-23-wizard-connect-usb");
            wz.Mode = ConnectMode.Manual;
            wz.Host = "192.0.2.20"; wz.Username = "upsmon";
            await Task.Delay(500);
            Save(w, dir, "p4-24-wizard-connect-manual");
            wz.Host = ""; wz.Username = "";
            wz.Mode = ConnectMode.Import;
            await Task.Delay(500);
            Save(w, dir, "p4-25-wizard-connect-import");
            wz.ImportUsername = "upsmon";
            wz.ApplyImportCommand.Execute(null);
            await Task.Delay(500);
            Save(w, dir, "p4-26-wizard-connect-imported");
            wz.NextCommand.Execute(null);
            await Task.Delay(600);
            Save(w, dir, "p4-27-wizard-shutdown");
            wz.NextCommand.Execute(null);
            await Task.Delay(600);
            Save(w, dir, "p4-28-wizard-startup");
            wz.NextCommand.Execute(null);
            await Task.Delay(700);
            Save(w, dir, "p4-29-wizard-done");
            wz.CloseCommand.Execute(null);
            await Task.Delay(1200);
            Save(w, dir, "p4-30-dashboard-after-setup");
            app.Tray?.SaveIcons(dir);
            File.WriteAllText(Path.Combine(dir, "run.txt"),
                $"update={upd.Phase} msg={upd.Message}\nservers={svc.Settings.Servers.Count} setup={svc.Settings.SetupCompleted} action={svc.LastActionResult}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
        }
        finally
        {
            app.Quit();
        }
    }

    private static void Save(Window w, string dir, string name)
    {
        if (!w.IsVisible) return;
        var scale = w.RenderScaling;
        var size = new PixelSize((int)(w.ClientSize.Width * scale), (int)(w.ClientSize.Height * scale));
        using var rtb = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        rtb.Render(w);
        rtb.Save(Path.Combine(dir, name + ".png"));
    }
}
