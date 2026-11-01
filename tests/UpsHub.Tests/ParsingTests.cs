using UpsHub.Core.Model;
using UpsHub.Core.Monitoring;
using UpsHub.Core.Nut;

namespace UpsHub.Tests;

public class ParsingTests
{
    [Fact]
    public void Tokenizer_handles_quotes_and_escapes()
    {
        var t = NutTokenizer.Split("VAR apc device.model \"Back-UPS \\\"XS\\\" 1400U\"");
        Assert.Equal(new[] { "VAR", "apc", "device.model", "Back-UPS \"XS\" 1400U" }, t);
        Assert.Equal(new[] { "UPS", "x", "" }, NutTokenizer.Split("UPS x \"\""));
        Assert.Equal("\"a\\\\b\\\"c\"", NutTokenizer.Quote("a\\b\"c"));
    }

    [Theory]
    [InlineData("OL", UpsStatus.OL)]
    [InlineData("OB DISCHRG LB", UpsStatus.OB | UpsStatus.DISCHRG | UpsStatus.LB)]
    [InlineData("OL CHRG RB", UpsStatus.OL | UpsStatus.CHRG | UpsStatus.RB)]
    [InlineData("FSD OB LB", UpsStatus.FSD | UpsStatus.OB | UpsStatus.LB)]
    [InlineData("OL WEIRDTOKEN", UpsStatus.OL)]
    [InlineData("", UpsStatus.None)]
    [InlineData(null, UpsStatus.None)]
    public void Status_parses_multi_token_flags(string? raw, UpsStatus expected) =>
        Assert.Equal(expected, UpsStatusParser.Parse(raw));

    [Fact]
    public void Snapshot_missing_values_are_null_not_zero()
    {
        var s = UpsSnapshot.FromVariables("u", new Dictionary<string, string> { ["ups.status"] = "OL" }, DateTimeOffset.UtcNow);
        Assert.Null(s.BatteryCharge);
        Assert.Null(s.Load);
        Assert.Null(s.RealPowerWatts);
        Assert.Equal(UpsHealth.Online, s.Health);
    }

    [Fact]
    public void Snapshot_power_fallbacks()
    {
        var now = DateTimeOffset.UtcNow;
        var direct = UpsSnapshot.FromVariables("u", new Dictionary<string, string> { ["ups.realpower"] = "150" }, now);
        Assert.Equal(150, direct.RealPowerWatts);
        Assert.False(direct.PowerIsEstimated);

        var fromNominal = UpsSnapshot.FromVariables("u", new Dictionary<string, string>
            { ["ups.load"] = "50", ["ups.realpower.nominal"] = "700" }, now);
        Assert.Equal(350, fromNominal.RealPowerWatts);
        Assert.True(fromNominal.PowerIsEstimated);

        var fromVa = UpsSnapshot.FromVariables("u", new Dictionary<string, string>
            { ["ups.load"] = "50", ["ups.power.nominal"] = "1000" }, now, powerFactor: 0.6);
        Assert.Equal(300, fromVa.RealPowerWatts!.Value, 3);
    }

    [Theory]
    [InlineData(UpsStatus.OB | UpsStatus.DISCHRG, UpsHealth.OnBattery)]
    [InlineData(UpsStatus.OB | UpsStatus.LB, UpsHealth.LowBattery)]
    [InlineData(UpsStatus.OB | UpsStatus.LB | UpsStatus.FSD, UpsHealth.ForcedShutdown)]
    [InlineData(UpsStatus.OL | UpsStatus.RB, UpsHealth.Alarm)]
    public void Health_mapping(UpsStatus status, UpsHealth health) =>
        Assert.Equal(health, new UpsSnapshot { UpsName = "u", Timestamp = default, Status = status }.Health);

    [Fact]
    public void Stale_snapshot_reports_unknown_health()
    {
        var s = new UpsSnapshot { UpsName = "u", Timestamp = default, Status = UpsStatus.OL, Stale = true };
        Assert.Equal(UpsHealth.Unknown, s.Health);
    }

    [Theory]
    [InlineData("apc@nas", "apc", "nas", 3493)]
    [InlineData("ups@192.0.2.50:3494", "ups", "192.0.2.50", 3494)]
    [InlineData("u@[fe80::1]:3493", "u", "fe80::1", 3493)]
    public void UpsAddress_parses(string text, string ups, string host, int port)
    {
        Assert.True(UpsAddress.TryParse(text, out var a));
        Assert.Equal(new UpsAddress(ups, host, port), a);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nohost@")]
    [InlineData("@host")]
    [InlineData("u@h:99999")]
    public void UpsAddress_rejects_garbage(string text) => Assert.False(UpsAddress.TryParse(text, out _));

    [Fact]
    public void Backoff_grows_and_caps()
    {
        var b = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), seed: 1);
        var delays = Enumerable.Range(0, 12).Select(_ => b.Next().TotalSeconds).ToList();
        Assert.InRange(delays[0], 0.5, 1.21);
        Assert.True(delays[3] > delays[0]);
        Assert.All(delays, d => Assert.True(d <= 60));
        Assert.True(delays[^1] >= 48);
        b.Reset();
        Assert.InRange(b.Next().TotalSeconds, 0.5, 1.21);
    }

    [Fact]
    public void Error_codes_parse()
    {
        Assert.Equal(NutError.DataStale, NutErrorParser.Parse("DATA-STALE"));
        Assert.Equal(NutError.AccessDenied, NutErrorParser.Parse("access-denied"));
        Assert.Equal(NutError.Unknown, NutErrorParser.Parse("SOMETHING-NEW"));
    }
}
