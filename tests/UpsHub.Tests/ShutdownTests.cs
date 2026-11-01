using UpsHub.Core.Model;
using UpsHub.Core.Settings;
using UpsHub.Core.Shutdown;

namespace UpsHub.Tests;

public class ShutdownTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static UpsSnapshot S(string status, double? charge = 100, double? runtime = 2000, bool stale = false) => new()
    {
        UpsName = "apc", Timestamp = T0, Status = UpsStatusParser.Parse(status), RawStatus = status,
        BatteryCharge = charge, BatteryRuntimeSeconds = runtime, Stale = stale,
    };

    private static ShutdownCoordinator C(Action<ShutdownRules>? tweak = null)
    {
        var r = new ShutdownRules { CountdownSeconds = 30, BatteryPercentBelow = 30, RuntimeSecondsBelow = 120 };
        tweak?.Invoke(r);
        return new ShutdownCoordinator(r);
    }

    [Fact]
    public void Ol_to_ob_to_lb_starts_countdown_and_fires_at_deadline()
    {
        var c = C();
        c.Evaluate(S("OL"), true, T0);
        Assert.Equal(ShutdownPhase.Idle, c.State.Phase);
        c.Evaluate(S("OB DISCHRG", 80), true, T0.AddSeconds(5));
        Assert.Equal(ShutdownPhase.Idle, c.State.Phase);
        c.Evaluate(S("OB DISCHRG LB", 18), true, T0.AddSeconds(60));
        Assert.Equal(ShutdownPhase.CountingDown, c.State.Phase);
        Assert.Equal("The UPS reports low battery", c.State.Reason);
        Assert.False(c.Tick(T0.AddSeconds(89)));
        Assert.True(c.Tick(T0.AddSeconds(90)));
        Assert.False(c.Tick(T0.AddSeconds(91)));          // fires once
        Assert.Equal(ShutdownPhase.Executing, c.State.Phase);
    }

    [Fact]
    public void Charge_and_runtime_thresholds()
    {
        var c = C(r => r.OnLowBatteryFlag = false);
        c.Evaluate(S("OB", 29), true, T0);
        Assert.Contains("29%", c.State.Reason);
        var d = C(r => { r.OnLowBatteryFlag = false; r.BatteryPercentBelow = 0; });
        d.Evaluate(S("OB", 90, 100), true, T0);
        Assert.Contains("Runtime", d.State.Reason);
    }

    [Fact]
    public void Thresholds_ignored_while_on_line()
    {
        var c = C();
        c.Evaluate(S("OL CHRG", 5, 30), true, T0);
        Assert.Equal(ShutdownPhase.Idle, c.State.Phase);
    }

    [Fact]
    public void Power_restored_cancels_countdown()
    {
        var c = C();
        c.Evaluate(S("OB LB", 10), true, T0);
        c.Evaluate(S("OL CHRG", 11), true, T0.AddSeconds(5));
        Assert.Equal(ShutdownPhase.Idle, c.State.Phase);
        Assert.Equal("Power restored", c.State.LastCancelReason);
        Assert.False(c.Tick(T0.AddSeconds(100)));
    }

    [Fact]
    public void User_cancel_suppresses_until_power_returns()
    {
        var c = C();
        c.Evaluate(S("OB LB", 10), true, T0);
        c.Cancel();
        c.Evaluate(S("OB LB", 8), true, T0.AddSeconds(10));
        Assert.Equal(ShutdownPhase.Suppressed, c.State.Phase);
        c.Evaluate(S("OL"), true, T0.AddSeconds(20));
        Assert.Equal(ShutdownPhase.Idle, c.State.Phase);
        c.Evaluate(S("OB LB", 8), true, T0.AddSeconds(30));
        Assert.Equal(ShutdownPhase.CountingDown, c.State.Phase);
    }

    [Fact]
    public void Fsd_overrides_suppression_when_followed()
    {
        var c = C();
        c.Evaluate(S("OB LB", 10), true, T0);
        c.Cancel();
        c.Evaluate(S("OB LB FSD", 9), true, T0.AddSeconds(5));
        Assert.Equal(ShutdownPhase.CountingDown, c.State.Phase);
        Assert.Contains("FSD", c.State.Reason);

        var ignore = C(r => { r.FollowFsd = false; r.OnLowBatteryFlag = false; r.BatteryPercentBelow = 0; r.RuntimeSecondsBelow = 0; });
        ignore.Evaluate(S("OL FSD"), true, T0);
        Assert.Equal(ShutdownPhase.Idle, ignore.State.Phase);
    }

    [Fact]
    public void Zero_countdown_acts_immediately()
    {
        var c = C(r => r.CountdownSeconds = 0);
        c.Evaluate(S("OB LB", 5), true, T0);
        Assert.True(c.Tick(T0));
    }

    [Fact]
    public void Extension_only_once()
    {
        var c = C(r => r.ExtensionSeconds = 60);
        c.Evaluate(S("OB LB", 5), true, T0);
        Assert.True(c.Extend());
        Assert.False(c.Extend());
        Assert.False(c.Tick(T0.AddSeconds(80)));
        Assert.True(c.Tick(T0.AddSeconds(90)));
    }

    [Fact]
    public void Stale_or_disconnected_data_never_triggers_by_default()
    {
        var c = C();
        c.Evaluate(S("OB LB", 1, 1, stale: true), true, T0);
        c.Evaluate(null, false, T0);
        Assert.Equal(ShutdownPhase.Idle, c.State.Phase);
    }

    [Fact]
    public void Lost_while_on_battery_option()
    {
        var c = C(r => r.ActIfLostWhileOnBattery = true);
        c.Evaluate(S("OB", 90), true, T0);
        c.Evaluate(null, false, T0.AddSeconds(5));
        Assert.Equal(ShutdownPhase.CountingDown, c.State.Phase);
        Assert.Contains("Lost contact", c.State.Reason);
    }

    [Fact]
    public void On_battery_duration_rule()
    {
        var c = C(r => { r.OnBatteryForSeconds = 300; r.BatteryPercentBelow = 0; r.RuntimeSecondsBelow = 0; });
        c.Evaluate(S("OB", 99), true, T0);
        c.Evaluate(S("OB", 98), true, T0.AddSeconds(299));
        Assert.Equal(ShutdownPhase.Idle, c.State.Phase);
        c.Evaluate(S("OB", 97), true, T0.AddSeconds(300));
        Assert.Equal(ShutdownPhase.CountingDown, c.State.Phase);
    }

    [Fact]
    public void Disabled_rules_cancel_running_countdown()
    {
        var c = C();
        c.Evaluate(S("OB LB", 5), true, T0);
        c.Rules.Enabled = false;
        c.Evaluate(S("OB LB", 5), true, T0.AddSeconds(1));
        Assert.Equal(ShutdownPhase.Idle, c.State.Phase);
    }
}
