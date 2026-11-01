using UpsHub.Core.Local;
using UpsHub.Core.Local.Hid;
using UpsHub.Core.Model;
using UpsHub.Core.Monitoring;
using UpsHub.Core.Settings;

namespace UpsHub.Tests;

/// <summary>Report-descriptor parser against the self-made fixture in <see cref="SyntheticHid"/>.</summary>
public class HidDescriptorTests
{
    private static HidReportDescriptor Apc() => HidReportDescriptor.Parse(SyntheticHid.ApcBackUps().Descriptor);

    [Fact]
    public void Builder_emits_spec_short_items()
    {
        var b = new HidDescriptorBuilder()
            .Application(0x84, 0x04).ReportId(1).Usage(0x84, 0x35).ReportSize(8).ReportCount(1)
            .LogicalMin(0).LogicalMax(255).Feature().End();
        // 255 does not fit a signed byte, so Logical Maximum takes two bytes (26 ff 00), as the spec requires.
        Assert.Equal(HidReportDescriptor.ParseHex("05 84 09 04 a1 01 85 01 09 35 75 08 95 01 15 00 26 ff 00 b1 02 c0"), b.ToArray());
        var f = Assert.Single(HidReportDescriptor.Parse(b.ToArray()).Fields);
        Assert.Equal(255, f.LogMax);
        Assert.False(f.AssumedLogMax);
    }

    [Fact]
    public void Synthetic_descriptor_parses_into_power_device_paths()
    {
        var cap = SyntheticHid.ApcBackUps();
        var d = HidReportDescriptor.Parse(cap.Descriptor);
        Assert.Empty(d.Warnings);
        Assert.Equal(cap.Objects, d.Fields.Count);
        Assert.All(d.Fields, f => Assert.StartsWith("UPS.", HidUsageTables.PathToString(f.Path)));
        foreach (var path in new[] { "UPS.PowerSummary.RemainingCapacity", "UPS.PowerSummary.RunTimeToEmpty",
                     "UPS.PowerSummary.PresentStatus.ACPresent", "UPS.PowerSummary.PresentStatus.NeedReplacement",
                     "UPS.Battery.ManufacturerDate", "UPS.Input.HighVoltageTransfer", "UPS.PowerConverter.PercentLoad" })
            Assert.True(d.Find(path, HidReportType.Feature) is not null, path);
        Assert.NotNull(d.Find("UPS.PowerSummary.RemainingCapacity", HidReportType.Input));
        Assert.Contains(d.Fields, f => HidUsageTables.PathToString(f.Path, HidUsageTables.WithVendor(HidUsageTables.Apc)) == "UPS.Input.APCSensitivity");
    }

    [Fact]
    public void Every_fixture_report_decodes_to_the_value_written()
    {
        var cap = SyntheticHid.ApcBackUps();
        var d = HidReportDescriptor.Parse(cap.Descriptor);
        Assert.NotEmpty(cap.Items);
        foreach (var it in cap.Items)
        {
            var f = d.Fields.Single(x => x.Type == it.Type && x.ReportId == it.ReportId && x.Offset == it.Offset
                                         && HidUsageTables.PathToString(x.Path) == it.Path);
            Assert.Equal(it.Size, f.Size);
            Assert.True(Math.Abs(f.ToPhysical(f.Extract(it.Report)) - it.Value) < 0.006, it.Path);
        }
        Assert.Equal(13.6, cap.Items.First(i => i.Path == "UPS.PowerSummary.Voltage").Value, 6);
        Assert.Equal(-1, cap.Items.First(i => i.Path == "UPS.PowerSummary.DelayBeforeShutdown").Value);
    }

    [Fact]
    public void Units_and_exponents_follow_the_power_device_spec()
    {
        var d = Apc();
        var volt = d.Find("UPS.PowerSummary.Voltage", HidReportType.Feature)!;
        Assert.Equal(0x00F0D121u, volt.Unit);       // volt, in SI-linear base units
        Assert.Equal(5, volt.UnitExp);
        Assert.Equal(-2, volt.EffectiveExponent);   // 10^5 base units = 10^-2 V
        Assert.Equal(13.58, volt.ToPhysical(1358), 6);
        var input = d.Find("UPS.Input.Voltage", HidReportType.Feature)!;
        Assert.Equal(0, input.EffectiveExponent);   // 10^7 base units = 1 V
        Assert.Equal(0x1001u, d.Find("UPS.PowerSummary.RunTimeToEmpty", HidReportType.Feature)!.Unit);
    }

    [Fact]
    public void Misencoded_logmax_is_read_as_unsigned_like_nut()
    {
        // Logical Min 0, Logical Max FF (1 byte, so -1 when signed); one 8-bit Feature field.
        var desc = HidReportDescriptor.ParseHex("05 84 09 04 a1 01 85 01 09 35 75 08 95 01 15 00 25 ff b1 02 c0");
        var d = HidReportDescriptor.Parse(desc);
        var f = Assert.Single(d.Fields);
        Assert.True(f.AssumedLogMax);
        Assert.Equal(255, f.LogMax);
        Assert.Equal(200, f.Extract(new byte[] { 0x01, 200 }));
        Assert.NotEmpty(d.Warnings);
    }

    [Fact]
    public void Signed_fields_are_sign_extended()
    {
        // DelayBeforeShutdown: 16 bit, LogMin -1 (ff ff), LogMax 7fff
        var d = HidReportDescriptor.Parse(HidReportDescriptor.ParseHex(
            "05 84 09 04 a1 01 85 15 09 57 75 10 95 01 16 ff ff 26 ff 7f b1 a2 c0"));
        var f = Assert.Single(d.Fields);
        Assert.Equal(-1, f.LogMin);
        Assert.Equal(-1, f.Extract(new byte[] { 0x15, 0xff, 0xff }));
        Assert.Equal(20, f.Extract(new byte[] { 0x15, 20, 0 }));
    }

    [Fact]
    public void Bit_fields_pack_after_the_report_id()
    {
        var d = Apc();
        var flags = d.Fields.Where(f => f.ReportId == 0x16 && f.Type == HidReportType.Input).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, flags.Select(f => f.Offset));
        Assert.All(flags, f => Assert.Equal(1, f.Size));
        Assert.Equal(4, d.ReportLength(HidReportType.Input, 0x16));   // 9 flags + 23 bits padding
    }

    [Fact]
    public void Write_then_extract_round_trips()
    {
        var d = Apc();
        var f = d.Find("UPS.Input.Voltage", HidReportType.Feature)!;
        var buf = new byte[d.ReportLength(f.Type, f.ReportId) + 1];
        buf[0] = f.ReportId;
        f.Write(buf, 229);
        Assert.Equal(229, f.Extract(buf));
    }

    [Fact]
    public void Truncated_or_unbalanced_descriptors_do_not_throw()
    {
        var full = SyntheticHid.ApcBackUps().Descriptor;
        for (int n = 0; n < full.Length; n += 7)
            HidReportDescriptor.Parse(full.AsSpan(0, n));
        var d = HidReportDescriptor.Parse(HidReportDescriptor.ParseHex("c0 c0 09"));
        Assert.Empty(d.Fields);
    }
}

/// <summary>APC mapping (apc-hid) against the synthetic Back-UPS fixture.</summary>
public class ApcMappingTests
{
    private static RawHidUpsDevice Fixture(HidCapture cap, string? serial = "9B2019A14274")
    {
        var d = HidReportDescriptor.Parse(cap.Descriptor);
        var reports = cap.LastReports();
        return new RawHidUpsDevice(new HidDeviceIdentity(cap.VendorId, cap.ProductId, cap.Vendor, cap.Product, serial, "test"),
            d, (t, id, _) => reports.GetValueOrDefault((t, id)), i => cap.Strings.GetValueOrDefault(i));
    }

    [Fact]
    public void Synthetic_back_ups_maps_to_nut_variables()
    {
        var drv = new HidUpsDriver(Fixture(SyntheticHid.ApcBackUps()));
        Assert.Same(HidSubdrivers.Apc, drv.Subdriver);
        Assert.False(drv.DescriptorFixed);
        var v = drv.Poll();
        var expected = new Dictionary<string, string>
        {
            ["battery.charge"] = "100", ["battery.charge.low"] = "10", ["battery.runtime"] = "2400",
            ["battery.runtime.low"] = "120", ["battery.voltage"] = "13.6", ["battery.voltage.nominal"] = "12.0",
            ["battery.type"] = "PbAc", ["battery.mfr.date"] = "2025/03/14",
            ["input.voltage"] = "232.0", ["input.voltage.nominal"] = "230", ["input.transfer.low"] = "180",
            ["input.transfer.high"] = "266", ["input.sensitivity"] = "medium",
            ["ups.load"] = "12", ["ups.realpower.nominal"] = "405", ["ups.timer.shutdown"] = "-1",
            ["ups.beeper.status"] = "enabled", ["ups.test.result"] = "No test initiated",
            ["ups.status"] = "OL", ["device.mfr"] = "American Power Conversion", ["device.model"] = "Back-UPS ES 700",
            ["device.serial"] = "9B2019A14274",
        };
        foreach (var (k, want) in expected)
        {
            Assert.True(v.ContainsKey(k), $"missing {k}");
            Assert.True(want == v[k], $"{k}: got '{v[k]}', expected '{want}'");
        }
    }

    [Fact]
    public void Back_ups_500_firmware_is_split_out_of_the_product_string()
    {
        var vars = new HidUpsDriver(Fixture(SyntheticHid.ApcBackUps("Back-UPS 500 FW: 6.5.I USB FW: c1"))).Poll();
        Assert.Equal("Back-UPS 500", vars["device.model"]);
        Assert.Equal("6.5.I", vars["ups.firmware"]);
        Assert.Equal("c1", vars["ups.firmware.aux"]);
        Assert.Equal("100", vars["battery.charge"]);
        Assert.Equal("13.6", vars["battery.voltage"]);
        Assert.Equal("OL", vars["ups.status"].Split(' ')[0]);
    }

    [Fact]
    public void Status_flags_follow_the_reports()
    {
        var dev = ReplayHidDevice.Create(SyntheticHid.ApcBackUps());
        var drv = new HidUpsDriver(dev);

        dev.Apply(acPresent: true, charging: false, charge: 100);
        Assert.Equal("OL", drv.Poll()["ups.status"]);

        dev.Apply(acPresent: false, charge: 64, runtime: 900);
        var v = drv.Poll();
        Assert.Equal("OB DISCHRG", v["ups.status"]);
        Assert.Equal("64", v["battery.charge"]);
        Assert.Equal("900", v["battery.runtime"]);

        dev.Apply(lowBattery: true, charge: 9);
        Assert.Equal("OB DISCHRG LB", drv.Poll()["ups.status"]);

        dev.Set("UPS.PowerSummary.PresentStatus.NeedReplacement", 1);
        dev.Set("UPS.PowerSummary.PresentStatus.Overload", 1);
        v = drv.Poll();
        Assert.Equal("OB DISCHRG LB OVER RB ALARM", v["ups.status"]);
        Assert.Contains("Replace battery!", v["ups.alarm"]);

        dev.Set("UPS.PowerSummary.PresentStatus.NeedReplacement", 0);
        dev.Set("UPS.PowerSummary.PresentStatus.Overload", 0);
        dev.Apply(acPresent: true, lowBattery: false, charging: true, charge: 40);
        Assert.Equal("OL CHRG", drv.Poll()["ups.status"]);
    }

    [Fact]
    public void Status_maps_to_the_same_health_as_nut_servers()
    {
        var dev = ReplayHidDevice.Create(SyntheticHid.ApcBackUps());
        var drv = new HidUpsDriver(dev);
        UpsHealth H() => UpsSnapshot.FromVariables("backups", drv.Poll(), DateTimeOffset.UtcNow).Health;
        dev.Apply(acPresent: true, charging: false);
        Assert.Equal(UpsHealth.Online, H());
        dev.Apply(acPresent: false);
        Assert.Equal(UpsHealth.OnBattery, H());
        dev.Apply(lowBattery: true);
        Assert.Equal(UpsHealth.LowBattery, H());
    }

    [Fact]
    public void Xs1400u_voltage_range_is_repaired()
    {
        // Back-UPS XS 1400U: input voltage declared 0..255 but HVT goes to 302 V.
        var desc = HidReportDescriptor.ParseHex(
            "05 84 09 04 a1 01 09 1a a1 00 " +
            "85 30 09 40 75 08 95 01 15 00 25 7f b1 02 " +   // ConfigVoltage 0..127
            "85 31 09 30 75 10 15 00 26 ff 00 b1 02 " +      // Voltage 0..255
            "85 33 09 54 75 10 15 00 26 2e 01 b1 02 c0 c0"); // HVT 0..302
        var d = HidReportDescriptor.Parse(desc);
        var reports = new Dictionary<(HidReportType, byte), byte[]>
        {
            [(HidReportType.Feature, 0x30)] = new byte[] { 0x30, 230 },
            [(HidReportType.Feature, 0x31)] = new byte[] { 0x31, 0x04, 0x01 },   // 260 V
            [(HidReportType.Feature, 0x33)] = new byte[] { 0x33, 0x1e, 0x01 },
        };
        var dev = new RawHidUpsDevice(new HidDeviceIdentity(0x051d, 0x0002, "APC", "Back-UPS XS 1400U", null, "t"), d,
            (t, id, _) => reports.GetValueOrDefault((t, id)));
        var drv = new HidUpsDriver(dev);
        Assert.True(drv.DescriptorFixed);
        var v = drv.Poll();
        Assert.Equal("260.0", v["input.voltage"]);
        Assert.Equal("230", v["input.voltage.nominal"]);
    }

    [Fact]
    public void Apc_dates_and_lookups()
    {
        var dev = ReplayHidDevice.Create(SyntheticHid.ApcBackUps());
        Assert.Equal("2002/10/22", HidSubdrivers.ApcDate(0x102202, dev));
        Assert.Equal("not set", HidSubdrivers.ApcDate(0, dev));
        Assert.Equal("2020/05/08", HidSubdrivers.Date(20648, dev));
        Assert.Equal("medium", HidSubdrivers.ApcSensitivity(1, dev));
        Assert.Equal("25.0", HidSubdrivers.KelvinCelsius(298.15, dev));
        Assert.Equal("25.0", HidSubdrivers.KelvinCelsius(25, dev));
    }
}

public class OtherVendorMappingTests
{
    // A compact CyberPower-style descriptor (report IDs as on CP1500 units).
    private static RawHidUpsDevice Cps(out Dictionary<(HidReportType, byte), byte[]> reports)
    {
        var desc = HidReportDescriptor.ParseHex(
            "05 84 09 04 a1 01 09 24 a1 00 " +
            "85 08 05 85 09 66 75 08 95 01 15 00 26 ff 00 b1 02 " +          // RemainingCapacity
            "85 08 09 68 75 10 27 ff ff 00 00 66 01 10 b1 02 " +              // RunTimeToEmpty (s)
            "85 0b 05 84 09 02 a1 02 05 85 75 01 95 04 25 01 09 d0 09 44 09 45 09 42 b1 02 75 04 95 01 b1 01 c0 " +
            "c0 05 84 09 1a a1 00 85 0f 09 30 75 08 15 00 26 ff 00 65 00 b1 02 " + // Input.Voltage
            "85 10 09 54 15 00 26 8c 00 b1 02 c0 " +                                // HVT 0..140
            "09 1c a1 00 85 12 09 30 15 00 26 8c 00 b1 02 85 13 09 35 26 ff 00 b1 02 c0 c0");
        var d = HidReportDescriptor.Parse(desc);
        var r = new Dictionary<(HidReportType, byte), byte[]>
        {
            [(HidReportType.Feature, 0x08)] = new byte[] { 0x08, 105, 0x10, 0x0e },  // 105 % (clamped), 3600 s
            [(HidReportType.Feature, 0x0b)] = new byte[] { 0x0b, 0b0001 },            // ACPresent
            [(HidReportType.Feature, 0x0f)] = new byte[] { 0x0f, 121 },
            [(HidReportType.Feature, 0x10)] = new byte[] { 0x10, 140 },
            [(HidReportType.Feature, 0x12)] = new byte[] { 0x12, 120 },
            [(HidReportType.Feature, 0x13)] = new byte[] { 0x13, 14 },
        };
        reports = r;
        return new RawHidUpsDevice(new HidDeviceIdentity(0x0764, 0x0501, "CPS", "CP1500PFCLCD", "CXXKV2000123", "t"), d,
            (t, id, _) => r.GetValueOrDefault((t, id)));
    }

    [Fact]
    public void CyberPower_mapping_and_output_voltage_fix()
    {
        var drv = new HidUpsDriver(Cps(out var reports));
        Assert.Same(HidSubdrivers.CyberPower, drv.Subdriver);
        Assert.True(drv.DescriptorFixed);   // output voltage shared the HVT range
        var v = drv.Poll();
        Assert.Equal("100", v["battery.charge"]);
        Assert.Equal("3600", v["battery.runtime"]);
        Assert.Equal("121.0", v["input.voltage"]);
        Assert.Equal("120.0", v["output.voltage"]);
        Assert.Equal("14", v["ups.load"]);
        Assert.Equal("OL", v["ups.status"]);
        Assert.Equal("CPS", v["device.mfr"]);

        reports[(HidReportType.Feature, 0x0b)] = new byte[] { 0x0b, 0b1100 };   // !AC, Discharging, LowBatt
        Assert.Equal("OB DISCHRG LB", drv.Poll()["ups.status"]);
    }

    [Fact]
    public void Eaton_indexed_collections_resolve()
    {
        // UPS.PowerConverter.Input.[1].Voltage and UPS.PowerSummary.RemainingCapacity
        var desc = HidReportDescriptor.ParseHex(
            "05 84 09 04 a1 01 09 16 a1 00 09 1a a1 81 85 01 09 30 75 10 95 01 15 00 26 ff 7f b1 02 c0 c0 " +
            "09 24 a1 00 85 02 05 85 09 66 75 08 15 00 25 64 b1 02 85 03 05 84 09 02 a1 02 05 85 09 d0 75 01 25 01 b1 02 75 07 b1 01 c0 c0 c0");
        var d = HidReportDescriptor.Parse(desc);
        Assert.Contains(d.Fields, f => HidUsageTables.PathToString(f.Path) == "UPS.PowerConverter.Input.[1].Voltage");
        var r = new Dictionary<(HidReportType, byte), byte[]>
        {
            [(HidReportType.Feature, 1)] = new byte[] { 1, 0xe6, 0x00 },
            [(HidReportType.Feature, 2)] = new byte[] { 2, 87 },
            [(HidReportType.Feature, 3)] = new byte[] { 3, 1 },
        };
        var drv = new HidUpsDriver(new RawHidUpsDevice(new HidDeviceIdentity(0x0463, 0xffff, "EATON", "Ellipse ECO", null, "t"), d,
            (t, id, _) => r.GetValueOrDefault((t, id))));
        Assert.Same(HidSubdrivers.EatonMge, drv.Subdriver);
        var v = drv.Poll();
        Assert.Equal("230.0", v["input.voltage"]);
        Assert.Equal("87", v["battery.charge"]);
        Assert.Equal("OL", v["ups.status"]);
    }

    [Fact]
    public void Unknown_vendors_use_the_generic_mapping()
    {
        Assert.Same(HidSubdrivers.Generic, HidSubdrivers.For(0x1234));
        var cap = SyntheticHid.ApcBackUps();
        var d = HidReportDescriptor.Parse(cap.Descriptor);
        var rep = cap.LastReports();
        var drv = new HidUpsDriver(new RawHidUpsDevice(new HidDeviceIdentity(0x1234, 0x0001, "Acme", "UPS 600", null, "t"), d,
            (t, id, _) => rep.GetValueOrDefault((t, id))));
        Assert.Same(HidSubdrivers.Generic, drv.Subdriver);
        var v = drv.Poll();
        Assert.Equal("100", v["battery.charge"]);
        Assert.Equal("232.0", v["input.voltage"]);
        Assert.StartsWith("OL", v["ups.status"]);
    }
}

public class MegatecTests
{
    [Fact]
    public void Parses_q1_and_estimates_charge()
    {
        var q = MegatecProtocol.ParseQ1("(229.0 229.0 229.0 018 50.0 13.50 25.0 00001000")!;
        var f = MegatecProtocol.ParseF("#230.0 004 12.00 50.0");
        var i = MegatecProtocol.ParseI("#MEGATEC         UPS 1000K  V1.0      ");
        var v = MegatecProtocol.ToVariables(q, f, i);
        Assert.Equal("OL", v["ups.status"]);
        Assert.Equal("18", v["ups.load"]);
        Assert.Equal("100", v["battery.charge"]);
        Assert.Equal("MEGATEC", v["device.mfr"]);
        Assert.Equal("UPS 1000K", v["device.model"]);

        var ob = MegatecProtocol.ToVariables(MegatecProtocol.ParseQ1("(000.0 000.0 229.0 020 50.0 2.00 25.0 11001000")!, f, null);
        Assert.Equal("OB LB", ob["ups.status"]);
        Assert.Equal("12.00", ob["battery.voltage"]);   // 2.00 V per cell x 6
        Assert.Equal("62", ob["battery.charge"]);   // (12.0 - 10.4) / (13.0 - 10.4)
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NAK")]
    [InlineData("(229.0 229.0 229.0 018 50.0 13.50 25.0 0000100")]
    [InlineData("(229.0 xx 229.0 018 50.0 13.50 25.0 00001000")]
    public void Rejects_bad_replies(string? reply) => Assert.Null(MegatecProtocol.ParseQ1(reply));
}

public class LocalMonitorTests
{
    private static ServerSettings Local(string model = "BK650M2-CH") => new()
    {
        Kind = UpsSourceKind.Local, Host = SettingsStore.LocalHost, DisplayName = "APC on USB",
        Local = new LocalDeviceSettings { Tier = LocalTier.Replay, DeviceKey = "051d:0002:SYN0001400U", Vendor = "American Power Conversion", Model = model },
    };

    [Fact]
    public async Task Publishes_snapshots_like_a_nut_server()
    {
        var cfg = Local();
        await using var m = new LocalUpsMonitor(cfg, () => new ReplayUpsSource(), TimeSpan.FromMilliseconds(50));
        var got = new TaskCompletionSource<(UpsAddress, UpsSnapshot)>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.SnapshotUpdated += (a, s) => got.TrySetResult((a, s));
        m.Start();
        var (addr, snap) = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("backups@usb", addr.ToString());
        Assert.Equal(UpsHealth.Online, snap.Health);
        Assert.Equal(100, snap.BatteryCharge);
        Assert.Equal("American Power Conversion", snap.Manufacturer);
        Assert.Equal(ConnectionState.Connected, m.Status.State);
        Assert.Equal("USB · APC Back-UPS ES 700", LocalNames.SourceLabel(cfg.Local!, snap));   // model as the device reports it
    }

    [Fact]
    public async Task Unplugged_device_goes_stale_then_recovers()
    {
        int opens = 0;
        bool unplugged = false;
        var src = new FlakySource(() => unplugged);
        await using var m = new LocalUpsMonitor(Local(), () => { opens++; return src; }, TimeSpan.FromMilliseconds(30),
            backoff: new Backoff(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40)));
        var stale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.SnapshotUpdated += (_, s) =>
        {
            if (s.Stale) stale.TrySetResult();
            else if (opens > 1) fresh.TrySetResult();
        };
        m.Start();
        await Task.Delay(120);
        unplugged = true;
        await stale.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectionState.Reconnecting, m.Status.State);
        unplugged = false;
        await fresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(opens >= 2);
    }

    private sealed class FlakySource(Func<bool> unplugged) : ILocalUpsSource
    {
        public string Kind => "test";
        public void Open() { if (unplugged()) throw new FileNotFoundException(); }
        public IReadOnlyDictionary<string, string> Read() => unplugged() ? throw new IOException("gone")
            : new Dictionary<string, string> { ["ups.status"] = "OL", ["battery.charge"] = "90" };
        public void Dispose() { }
    }

    [Fact]
    public async Task Missing_platform_support_reports_a_message_and_retries()
    {
        await using var m = new LocalUpsMonitor(Local(), () => null, TimeSpan.FromMilliseconds(30),
            backoff: new Backoff(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40)));
        var seen = new TaskCompletionSource<ServerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.StatusChanged += s => { if (s.State == ConnectionState.Reconnecting) seen.TrySetResult(s); };
        m.Start();
        var st = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("isn't supported", st.Message);
    }

    [Fact]
    public void Local_devices_round_trip_through_settings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "upshub-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            var s = new AppSettings();
            s.Servers.Add(new ServerSettings { Host = "10.0.0.2" });
            s.Servers.Add(Local());
            store.Save(s);
            Assert.DoesNotContain("isLocal", File.ReadAllText(store.FilePath));
            var back = store.Load();
            Assert.Equal(2, back.Servers.Count);
            Assert.False(back.Servers[0].IsLocal);
            var l = back.Servers[1];
            Assert.True(l.IsLocal);
            Assert.Equal(LocalTier.Replay, l.Local!.Tier);
            Assert.Equal("051d:0002:SYN0001400U", l.Local.DeviceKey);

            // A hand-edited file with a local entry missing its details still loads.
            File.WriteAllText(store.FilePath, """{"servers":[{"kind":"Local"}]}""");
            var fixedUp = store.Load();
            Assert.NotNull(fixedUp.Servers[0].Local);
            Assert.Equal(SettingsStore.LocalHost, fixedUp.Servers[0].Host);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData("American Power Conversion", "BK650M2-CH", "backups", "USB · APC Back-UPS BK650M2-CH")]
    [InlineData("American Power Conversion", "Back-UPS XS 1400U", "backups", "USB · APC Back-UPS XS 1400U")]
    [InlineData("American Power Conversion", "Smart-UPS 1500", "smartups", "USB · APC Smart-UPS 1500")]
    [InlineData("CPS", "CP1500PFCLCD", "cyberpower", "USB · CyberPower CP1500PFCLCD")]
    [InlineData("EATON", "Ellipse ECO", "eaton", "USB · Eaton Ellipse ECO")]
    public void Names_and_labels(string mfr, string model, string ups, string label)
    {
        Assert.Equal(ups, LocalNames.UpsName(mfr, model));
        Assert.Equal(label, LocalNames.SourceLabel(new LocalDeviceSettings { Vendor = mfr, Model = model }));
    }
}

public class SourceSwitchingTests
{
    [Fact]
    public void Nut_and_local_sources_round_trip_through_settings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "upshub-test-" + Guid.NewGuid().ToString("n"));
        try
        {
            var store = new SettingsStore(dir);
            var s = new AppSettings();
            s.Servers.Add(new ServerSettings { Host = "192.0.2.20", DisplayName = "Proxmox" });
            s.Servers.Add(new ServerSettings
            {
                Kind = UpsSourceKind.Local, Host = SettingsStore.LocalHost, UpsNames = { "backups" },
                Local = new LocalDeviceSettings { Tier = LocalTier.Hid, DeviceKey = "051d:0002:SYN0001400U", DevicePath = @"\\?\hid#vid_051d&pid_0002", Vendor = "American Power Conversion", Model = "Back-UPS ES 700G" },
            });
            s.Shutdown.PrimaryUps = "backups@usb:3493";
            store.Save(s);

            var back = store.Load();
            Assert.Equal(2, back.Servers.Count);
            Assert.False(back.Servers[0].IsLocal);
            var local = back.Servers[1];
            Assert.True(local.IsLocal);
            Assert.Equal(LocalTier.Hid, local.Local!.Tier);
            Assert.Equal("051d:0002:SYN0001400U", local.Local.DeviceKey);
            Assert.Equal("USB · APC Back-UPS ES 700G", LocalNames.SourceLabel(local.Local));

            // Switching the same entry back to NUT keeps working (Local is ignored for NUT entries).
            local.Kind = UpsSourceKind.Nut; local.Host = "10.0.0.5";
            store.Save(back);
            Assert.False(store.Load().Servers[1].IsLocal);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Local_entry_missing_details_is_repaired_on_load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "upshub-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "servers": [ { "kind": "Local", "host": "" } ] }""");
            var srv = new SettingsStore(dir).Load().Servers.Single();
            Assert.True(srv.IsLocal);
            Assert.NotNull(srv.Local);
            Assert.Equal(SettingsStore.LocalHost, srv.Host);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Shutdown_rules_act_on_a_local_ups_exactly_like_a_nut_one()
    {
        var cfg = new ServerSettings
        {
            Kind = UpsSourceKind.Local, Host = SettingsStore.LocalHost,
            Local = new LocalDeviceSettings { Tier = LocalTier.Replay, DeviceKey = "051d:0002:x", Vendor = "American Power Conversion", Model = "BK650M2-CH" },
        };
        double charge = 60;
        var src = new ReplayUpsSource(script: (d, t) =>
        {
            charge -= 10;
            d.Apply(acPresent: false, charging: false, charge: Math.Max(charge, 5), runtime: 300, load: 30, inputVoltage: 0);
        });
        await using var m = new LocalUpsMonitor(cfg, () => src, TimeSpan.FromMilliseconds(20));
        var coordinator = new UpsHub.Core.Shutdown.ShutdownCoordinator(new ShutdownRules { BatteryPercentBelow = 30, CountdownSeconds = 30 });
        var counting = new TaskCompletionSource<UpsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.SnapshotUpdated += (_, s) =>
        {
            lock (coordinator)
                if (coordinator.Evaluate(s, true, DateTimeOffset.UtcNow).Phase == UpsHub.Core.Shutdown.ShutdownPhase.CountingDown)
                    counting.TrySetResult(s);
        };
        m.Start();
        var snap = await counting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpsHealth.OnBattery, snap.Health);
        Assert.True(snap.BatteryCharge < 30);
    }
}
