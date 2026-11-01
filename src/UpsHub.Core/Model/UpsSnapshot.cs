using System.Globalization;

namespace UpsHub.Core.Model;

/// <summary>
/// One poll of one UPS. Every reading is nullable: a missing variable means "unknown", never zero
/// (zeros caused bogus gauges and reconnect loops in WinNUT).
/// </summary>
public sealed record UpsSnapshot
{
    public required string UpsName { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public bool Stale { get; init; }
    public string RawStatus { get; init; } = "";
    public UpsStatus Status { get; init; }
    public double? BatteryCharge { get; init; }        // %
    public double? BatteryRuntimeSeconds { get; init; }
    public bool RuntimeIsEstimated { get; init; }
    public double? BatteryVoltage { get; init; }
    public double? Load { get; init; }                 // %
    public double? InputVoltage { get; init; }
    public double? InputFrequency { get; init; }
    public double? OutputVoltage { get; init; }
    public double? OutputFrequency { get; init; }
    public double? RealPowerWatts { get; init; }
    public bool PowerIsEstimated { get; init; }
    public double? NominalRealPower { get; init; }
    public double? NominalApparentPower { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Serial { get; init; }
    public string? DriverName { get; init; }
    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();

    public UpsHealth Health => Stale ? UpsHealth.Unknown : UpsStatusParser.HealthOf(Status);

    public static double? Num(IReadOnlyDictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : null;

    public static UpsSnapshot FromVariables(string ups, IReadOnlyDictionary<string, string> v, DateTimeOffset now,
        double powerFactor = 0.6)
    {
        v.TryGetValue("ups.status", out var rawStatus);
        var charge = Num(v, "battery.charge");
        var load = Num(v, "ups.load");

        // Runtime: prefer the UPS's own figure.
        var runtime = Num(v, "battery.runtime");
        bool runtimeEstimated = false;

        // Power: ups.realpower > ups.power*PF > load% * nominal (realpower, else VA*PF).
        double? watts = Num(v, "ups.realpower");
        bool powerEstimated = false;
        var nomReal = Num(v, "ups.realpower.nominal");
        var nomVa = Num(v, "ups.power.nominal");
        if (watts is null && Num(v, "ups.power") is { } va) { watts = va * powerFactor; powerEstimated = true; }
        if (watts is null && load is { } l)
        {
            if (nomReal is { } nr) { watts = nr * l / 100.0; powerEstimated = true; }
            else if (nomVa is { } nv) { watts = nv * powerFactor * l / 100.0; powerEstimated = true; }
        }

        return new UpsSnapshot
        {
            UpsName = ups,
            Timestamp = now,
            RawStatus = rawStatus ?? "",
            Status = UpsStatusParser.Parse(rawStatus),
            BatteryCharge = charge,
            BatteryRuntimeSeconds = runtime,
            RuntimeIsEstimated = runtimeEstimated,
            BatteryVoltage = Num(v, "battery.voltage"),
            Load = load,
            InputVoltage = Num(v, "input.voltage"),
            InputFrequency = Num(v, "input.frequency"),
            OutputVoltage = Num(v, "output.voltage"),
            OutputFrequency = Num(v, "output.frequency"),
            RealPowerWatts = watts,
            PowerIsEstimated = powerEstimated,
            NominalRealPower = nomReal,
            NominalApparentPower = nomVa,
            Manufacturer = v.GetValueOrDefault("device.mfr") ?? v.GetValueOrDefault("ups.mfr"),
            Model = v.GetValueOrDefault("device.model") ?? v.GetValueOrDefault("ups.model"),
            Serial = v.GetValueOrDefault("device.serial") ?? v.GetValueOrDefault("ups.serial"),
            DriverName = v.GetValueOrDefault("driver.name"),
            Variables = v,
        };
    }

    public static UpsSnapshot StaleFor(string ups, DateTimeOffset now, UpsSnapshot? previous) =>
        (previous ?? new UpsSnapshot { UpsName = ups, Timestamp = now }) with { Timestamp = now, Stale = true };
}
