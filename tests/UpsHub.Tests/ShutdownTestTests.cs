using UpsHub.Core.Model;
using UpsHub.Core.Platform;
using UpsHub.Core.Settings;
using UpsHub.Core.Shutdown;

namespace UpsHub.Tests;

/// <summary>
/// Regression tests for the 0.1.0–0.3.0 bug where "Test countdown" performed the real action while the
/// UI said nothing would happen. Tests now perform the real configured action on purpose (behind a
/// confirmation in the UI), Cancel must always stop them, and the dry-run stand-in used by demo mode
/// must never reach a real power API.
/// </summary>
public class ShutdownTestTests
{
    private sealed class RecordingPower(bool supported) : IPowerActions
    {
        public List<ShutdownAction> Calls { get; } = new();
        public bool IsSupported => supported;
        public Task<string> ExecuteAsync(ShutdownAction action, bool force, CancellationToken ct = default)
        {
            Calls.Add(action);
            return Task.FromResult($"did {action}");
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static (ShutdownController C, RecordingPower P, ShutdownRules R) Make(bool real = true, int countdown = 30, ShutdownAction action = ShutdownAction.Hibernate)
    {
        var rules = new ShutdownRules { CountdownSeconds = countdown, Action = action };
        var power = new RecordingPower(real);
        return (new ShutdownController(new ShutdownCoordinator(rules), power, () => rules), power, rules);
    }

    private static UpsSnapshot Online() => new() { UpsName = "ups", Timestamp = T0, Status = UpsStatus.OL, BatteryCharge = 100 };

    [Fact]
    public async Task Test_runs_the_real_configured_action_once_at_the_deadline()
    {
        var (c, p, _) = Make(action: ShutdownAction.Hibernate);
        Assert.True(c.StartTest(T0));
        Assert.True(c.IsTest);
        Assert.Contains("really hibernate", c.Coordinator.State.Reason);
        Assert.Null(await c.TickAsync(T0.AddSeconds(29)));
        Assert.Empty(p.Calls);
        Assert.Equal("did Hibernate", await c.TickAsync(T0.AddSeconds(30)));
        Assert.Null(await c.TickAsync(T0.AddSeconds(31)));
        Assert.Equal(new[] { ShutdownAction.Hibernate }, p.Calls);
    }

    [Fact]
    public async Task Cancel_during_a_test_stops_it_for_good_and_real_outages_still_work()
    {
        var (c, p, _) = Make();
        c.StartTest(T0);
        c.Cancel();
        Assert.False(c.IsTest);
        Assert.Equal(ShutdownPhase.Idle, c.Coordinator.State.Phase);
        Assert.Null(await c.TickAsync(T0.AddMinutes(5)));
        Assert.Null(await c.NowAsync());
        Assert.Empty(p.Calls);

        // Not left "suppressed": a real low battery afterwards still starts a countdown.
        var lb = new UpsSnapshot { UpsName = "ups", Timestamp = T0, Status = UpsStatus.OB | UpsStatus.LB, BatteryCharge = 5 };
        Assert.Equal(ShutdownPhase.CountingDown, c.Evaluate(lb, true, T0.AddMinutes(6)).Phase);
    }

    [Fact]
    public async Task Now_during_a_test_runs_the_action_exactly_once()
    {
        var (c, p, _) = Make(action: ShutdownAction.Sleep);
        c.StartTest(T0);
        Assert.Equal("did Sleep", await c.NowAsync());
        Assert.Null(await c.NowAsync());
        Assert.Null(await c.TickAsync(T0.AddHours(1)));
        Assert.Single(p.Calls);
    }

    [Fact]
    public async Task Healthy_ups_readings_do_not_cancel_or_restart_a_test()
    {
        var (c, p, _) = Make();
        c.StartTest(T0);
        c.Evaluate(Online(), true, T0.AddSeconds(5));
        Assert.Equal(ShutdownPhase.CountingDown, c.Coordinator.State.Phase);
        Assert.False(c.StartTest(T0.AddSeconds(6)));   // no second test on top
        await c.TickAsync(T0.AddSeconds(30));
        Assert.Single(p.Calls);
    }

    [Fact]
    public void A_zero_warning_time_still_gives_time_to_cancel()
    {
        var (c, _, _) = Make(countdown: 0);
        c.StartTest(T0);
        Assert.Equal(T0.AddSeconds(ShutdownController.MinTestCountdownSeconds), c.Coordinator.State.Deadline);
    }

    [Fact]
    public async Task Dry_run_power_never_performs_anything_and_returns_to_idle()
    {
        var dry = new DryRunPowerActions();
        var rules = new ShutdownRules { CountdownSeconds = 10, Action = ShutdownAction.Shutdown };
        var c = new ShutdownController(new ShutdownCoordinator(rules), dry, () => rules);
        Assert.False(c.PerformsRealActions);
        c.StartTest(T0);
        var r = await c.TickAsync(T0.AddSeconds(10));
        Assert.StartsWith("[dry run]", r);
        Assert.Single(dry.Executed);
        Assert.Equal(ShutdownPhase.Idle, c.Coordinator.State.Phase);
    }
}

public class DemoSafetyTests
{
    [Theory]
    [InlineData("--demo")]
    [InlineData("--screenshots", "x")]
    [InlineData("--screenshots-discovery", "x")]
    [InlineData("--screenshots-local", "x")]
    public void Every_demo_and_screenshot_mode_is_a_dry_run(params string[] args)
    {
        var o = UpsHub.App.AppOptions.Parse(args);
        Assert.True(o.Demo);
        bool realCreated = false;
        var power = UpsHub.App.Platform.PlatformServices.PowerFor(o.Demo, () => { realCreated = true; return new DryRunPowerActions(); });
        Assert.IsType<DryRunPowerActions>(power);
        Assert.False(realCreated);
        Assert.False(power.IsSupported);
    }

    [Fact]
    public void Normal_start_uses_the_real_power_actions()
    {
        var o = UpsHub.App.AppOptions.Parse(new[] { "--minimized" });
        Assert.False(o.Demo);
        var real = new DryRunPowerActions();
        Assert.Same(real, UpsHub.App.Platform.PlatformServices.PowerFor(o.Demo, () => real));
    }

    [Fact]
    public void Platform_services_in_this_test_process_are_never_real() =>
        Assert.False(UpsHub.App.Platform.PlatformServices.Create(dryRunPower: true, iconPath: null).Power.IsSupported);
}
