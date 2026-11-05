using UpsHub.Core.Model;
using UpsHub.Core.Monitoring;

namespace UpsHub.Tests;

public class PowerEventTests
{
    private static readonly UpsAddress A = new("apc", "nut.example", 3493);
    private static UpsSnapshot S(string status, bool stale = false) =>
        UpsSnapshot.FromVariables("apc", new Dictionary<string, string> { ["ups.status"] = status }, DateTimeOffset.UtcNow) with { Stale = stale };

    [Fact]
    public void Online_at_start_is_silent_on_battery_at_start_notifies()
    {
        var d = new PowerEventDetector();
        Assert.Equal(PowerEvent.None, d.Observe(A, S("OL")));
        var d2 = new PowerEventDetector();
        Assert.Equal(PowerEvent.OnBattery, d2.Observe(A, S("OB DISCHRG")));
    }

    [Fact]
    public void Stale_blip_does_not_swallow_the_next_event()
    {
        var d = new PowerEventDetector();
        d.Observe(A, S("OL"));
        Assert.Equal(PowerEvent.None, d.Observe(A, S("OL", stale: true)));     // DATA-STALE / reconnect
        Assert.Equal(PowerEvent.OnBattery, d.Observe(A, S("OB")));           // used to be lost (Unknown -> OB)
        Assert.Equal(PowerEvent.None, d.Observe(A, S("OB", stale: true)));
        Assert.Equal(PowerEvent.PowerRestored, d.Observe(A, S("OL CHRG")));
    }

    [Fact]
    public void Full_outage_sequence()
    {
        var d = new PowerEventDetector();
        Assert.Equal(PowerEvent.None, d.Observe(A, S("OL")));
        Assert.Equal(PowerEvent.OnBattery, d.Observe(A, S("OB")));
        Assert.Equal(PowerEvent.None, d.Observe(A, S("OB")));
        Assert.Equal(PowerEvent.LowBattery, d.Observe(A, S("OB LB")));
        Assert.Equal(PowerEvent.ForcedShutdown, d.Observe(A, S("OB LB FSD")));
        Assert.Equal(PowerEvent.PowerRestored, d.Observe(A, S("OL")));
    }

    [Fact]
    public void Reconnect_text_is_never_terminal()
    {
        var now = DateTimeOffset.UtcNow;
        var st = new ServerStatus("x", ConnectionState.Reconnecting, "timeout", Attempt: 7, NextRetry: now.AddSeconds(29.2));
        Assert.Equal("Reconnecting (attempt 7, next try in 30s)", ConnectionText.Describe(st, now));
        Assert.Equal("Reconnecting (attempt 7, next try in 0s)", ConnectionText.Describe(st, now.AddMinutes(5)));
        Assert.Equal("Connected", ConnectionText.Describe(st with { State = ConnectionState.Connected }, now));
    }
}
