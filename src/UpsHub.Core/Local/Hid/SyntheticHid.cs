namespace UpsHub.Core.Local.Hid;

/// <summary>
/// Writes HID report descriptors item by item (HID 1.11 §6.2.2 short items), so test and demo
/// devices can be described from the HID Power Device spec (usage pages 0x84 / 0x85) by hand.
/// </summary>
public sealed class HidDescriptorBuilder
{
    private readonly List<byte> _b = new();
    private ushort? _page;

    public byte[] ToArray() => _b.ToArray();

    private HidDescriptorBuilder Item(byte prefix, long value, bool signed)
    {
        int size;
        if (signed) size = value is >= sbyte.MinValue and <= sbyte.MaxValue ? 1 : value is >= short.MinValue and <= short.MaxValue ? 2 : 4;
        else size = value is >= 0 and <= 0xFF ? 1 : value is >= 0 and <= 0xFFFF ? 2 : 4;
        _b.Add((byte)(prefix | (size == 4 ? 3 : size)));
        for (int i = 0; i < size; i++) _b.Add((byte)(value >> (8 * i)));
        return this;
    }

    public HidDescriptorBuilder UsagePage(ushort page) { _page = page; return Item(0x04, page, false); }
    public HidDescriptorBuilder Usage(ushort page, ushort usage) { if (_page != page) UsagePage(page); return Item(0x08, usage, false); }
    public HidDescriptorBuilder Collection(ushort page, ushort usage, byte kind) { Usage(page, usage); return Item(0xA0, kind, false); }
    public HidDescriptorBuilder Application(ushort page, ushort usage) => Collection(page, usage, 0x01);
    public HidDescriptorBuilder Physical(ushort page, ushort usage) => Collection(page, usage, 0x00);
    public HidDescriptorBuilder Logical(ushort page, ushort usage) => Collection(page, usage, 0x02);
    public HidDescriptorBuilder End() { _b.Add(0xC0); return this; }
    public HidDescriptorBuilder ReportId(byte id) => Item(0x84, id, false);
    public HidDescriptorBuilder ReportSize(int bits) => Item(0x74, bits, false);
    public HidDescriptorBuilder ReportCount(int n) => Item(0x94, n, false);
    public HidDescriptorBuilder LogicalMin(long v) => Item(0x14, v, true);
    public HidDescriptorBuilder LogicalMax(long v) => Item(0x24, v, true);
    public HidDescriptorBuilder Unit(uint unit) => unit == 0 ? Item(0x64, 0, false) : Item(0x64, unit, false);
    /// <summary>Unit exponent as the 4-bit two's complement nibble of HID 1.11 §6.2.2.7.</summary>
    public HidDescriptorBuilder UnitExponent(int e) => Item(0x54, e & 0x0F, false);
    /// <summary>Feature item: 0x02 = Data,Var,Abs; 0xA2 adds Volatile + No Preferred (as most UPSs declare).</summary>
    public HidDescriptorBuilder Feature(byte flags = 0x02) => Item(0xB0, flags, false);
    public HidDescriptorBuilder Input(byte flags = 0x02) => Item(0x80, flags, false);
    public HidDescriptorBuilder Constant(int bits, bool input = false)
    {
        ReportSize(bits); ReportCount(1);
        return input ? Input(0x01) : Feature(0x01);
    }
}

/// <summary>
/// Self-made HID UPS fixtures: descriptors written from the HID Power Device Class spec (USB-IF,
/// "Usage Tables for HID Power Devices" 1.0), laid out the way APC Back-UPS units structure theirs
/// (PowerSummary + PresentStatus flags in a Feature/Input pair, Battery, Input, PowerConverter),
/// with report values filled in by path. No vendor or third-party dumps are used.
/// </summary>
public static class SyntheticHid
{
    private const ushort Ups = 0x84, Bat = 0x85, ApcPage = 0xFF86;
    public const uint UnitVolt = 0x00F0D121, UnitWatt = 0x0000D121, UnitSecond = 0x00001001;

    public const string ApcVendor = "American Power Conversion";
    public const string ApcProduct = "Back-UPS ES 700";
    public const string ApcSerial = "SYN0000700";

    /// <summary>Indices of the string descriptors the fixture refers to.</summary>
    public const int StrChemistry = 4, StrProduct = 1, StrSerial = 2, StrVendor = 3;

    /// <summary>The APC Back-UPS fixture (VID 051d, PID 0002) on line, fully charged.</summary>
    public static HidCapture ApcBackUps(string product = ApcProduct)
    {
        var b = new HidDescriptorBuilder();
        void F(ushort page, ushort usage, byte id, int bits, long min, long max, bool alsoInput = false,
               uint unit = 0, int exp = 0)
        {
            b.ReportId(id).Usage(page, usage).ReportSize(bits).ReportCount(1).LogicalMin(min).LogicalMax(max)
             .Unit(unit).UnitExponent(exp).Feature(0xA2);
            if (alsoInput) b.Usage(page, usage).Input(0x82);
        }

        b.Application(Ups, 0x04);                                   // UPS
          b.Physical(Ups, 0x24);                                    //  PowerSummary
            F(Ups, 0xFE, 0x01, 8, 0, 255);                          //   iProduct
            F(Ups, 0xFF, 0x02, 8, 0, 255);                          //   iSerialNumber
            F(Bat, 0x89, 0x03, 8, 0, 255);                          //   iDeviceChemistry
            F(Ups, 0xFD, 0x04, 8, 0, 255);                          //   iManufacturer
            F(Bat, 0x8B, 0x05, 8, 0, 1);                            //   Rechargeable
            F(Bat, 0x2C, 0x06, 8, 0, 2);                            //   CapacityMode (2 = %)
            F(Bat, 0x83, 0x07, 8, 0, 100);                          //   DesignCapacity
            F(Bat, 0x67, 0x08, 8, 0, 100);                          //   FullChargeCapacity
            F(Bat, 0x66, 0x0C, 8, 0, 100, alsoInput: true);         //   RemainingCapacity
            F(Bat, 0x68, 0x0F, 16, 0, 0xFFFF, true, UnitSecond);    //   RunTimeToEmpty
            F(Bat, 0x29, 0x11, 8, 0, 100);                          //   RemainingCapacityLimit
            F(Bat, 0x2A, 0x12, 16, 0, 0x7FFF, false, UnitSecond);   //   RemainingTimeLimit
            F(Ups, 0x57, 0x15, 16, -1, 0x7FFF, false, UnitSecond);  //   DelayBeforeShutdown
            F(Ups, 0x5A, 0x18, 8, 1, 3);                            //   AudibleAlarmControl
            F(Ups, 0x40, 0x19, 16, 0, 0xFFFF, false, UnitVolt, 5);  //   ConfigVoltage (10 mV)
            F(Ups, 0x30, 0x1A, 16, 0, 0xFFFF, false, UnitVolt, 5);  //   Voltage (10 mV)
            // PresentStatus: 9 one-bit flags, Feature and Input, padded to 32 bits.
            b.Logical(Ups, 0x02);
            var flags = new (ushort P, ushort U)[]
            {
                (Bat, 0x44), (Bat, 0x45), (Bat, 0xD0), (Bat, 0xD1), (Bat, 0x42),   // Charging Discharging ACPresent BatteryPresent BelowRCL
                (Ups, 0x69), (Bat, 0x43), (Bat, 0x4B), (Ups, 0x65),               // ShutdownImminent RemainingTimeLimitExpired NeedReplacement Overload
            };
            foreach (var kind in new[] { false, true })
            {
                b.ReportId(0x16).ReportSize(1).ReportCount(1).LogicalMin(0).LogicalMax(1).Unit(0).UnitExponent(0);
                foreach (var (p, u) in flags) { b.Usage(p, u); if (kind) b.Input(0x82); else b.Feature(0xA2); }
                b.Constant(23, input: kind);
            }
            b.End();
          b.End();
          b.Physical(Ups, 0x12);                                    //  Battery
            F(Bat, 0x85, 0x20, 16, 0, 0xFFFF);                      //   ManufacturerDate
            F(Ups, 0x58, 0x21, 8, 0, 6);                            //   Test
            F(Ups, 0x40, 0x22, 16, 0, 0xFFFF, false, UnitVolt, 5);  //   ConfigVoltage
            F(Ups, 0x30, 0x23, 16, 0, 0xFFFF, false, UnitVolt, 5);  //   Voltage
          b.End();
          b.Physical(Ups, 0x1A);                                    //  Input
            F(Ups, 0x40, 0x30, 16, 0, 0x01FF, false, UnitVolt, 7);  //   ConfigVoltage (V)
            F(Ups, 0x30, 0x31, 16, 0, 0x01FF, false, UnitVolt, 7);  //   Voltage
            F(Ups, 0x53, 0x32, 16, 0, 0x01FF, false, UnitVolt, 7);  //   LowVoltageTransfer
            F(Ups, 0x54, 0x33, 16, 0, 0x01FF, false, UnitVolt, 7);  //   HighVoltageTransfer
            F(ApcPage, 0x61, 0x34, 8, 0, 2);                        //   APC vendor: sensitivity
          b.End();
          b.Physical(Ups, 0x16);                                    //  PowerConverter
            F(Ups, 0x35, 0x50, 8, 0, 100, alsoInput: true);         //   PercentLoad
            F(Ups, 0x44, 0x52, 16, 0, 0xFFFF, false, UnitWatt, 7);  //   ConfigActivePower (W)
          b.End();
        b.End();

        var descriptor = b.ToArray();
        var d = HidReportDescriptor.Parse(descriptor);
        var reports = new Dictionary<(HidReportType, byte), byte[]>();
        foreach (var (key, len) in d.ReportLengths)
            reports[key] = Init(new byte[len + 1], key.Item2);

        void Set(string path, double physical)
        {
            foreach (var f in d.Fields)
            {
                if (HidUsageTables.PathToString(f.Path, HidUsageTables.WithVendor(HidUsageTables.Apc)) != path
                    && HidUsageTables.PathToString(f.Path) != path) continue;
                int e = f.EffectiveExponent;
                f.Write(reports[(f.Type, f.ReportId)], (long)Math.Round(e == 0 ? physical : physical / Math.Pow(10, e)));
            }
        }

        Set("UPS.PowerSummary.iProduct", StrProduct);
        Set("UPS.PowerSummary.iSerialNumber", StrSerial);
        Set("UPS.PowerSummary.iDeviceChemistry", StrChemistry);
        Set("UPS.PowerSummary.iManufacturer", StrVendor);
        Set("UPS.PowerSummary.Rechargeable", 1);
        Set("UPS.PowerSummary.CapacityMode", 2);
        Set("UPS.PowerSummary.DesignCapacity", 100);
        Set("UPS.PowerSummary.FullChargeCapacity", 100);
        Set("UPS.PowerSummary.RemainingCapacity", 100);
        Set("UPS.PowerSummary.RunTimeToEmpty", 2400);
        Set("UPS.PowerSummary.RemainingCapacityLimit", 10);
        Set("UPS.PowerSummary.RemainingTimeLimit", 120);
        Set("UPS.PowerSummary.DelayBeforeShutdown", -1);
        Set("UPS.PowerSummary.AudibleAlarmControl", 2);
        Set("UPS.PowerSummary.ConfigVoltage", 12.0);
        Set("UPS.PowerSummary.Voltage", 13.6);
        Set("UPS.PowerSummary.PresentStatus.ACPresent", 1);
        Set("UPS.PowerSummary.PresentStatus.BatteryPresent", 1);
        Set("UPS.Battery.ManufacturerDate", (2025 - 1980) * 512 + 3 * 32 + 14);   // 2025/03/14, HID date encoding
        Set("UPS.Battery.Test", 6);
        Set("UPS.Battery.ConfigVoltage", 12.0);
        Set("UPS.Battery.Voltage", 13.6);
        Set("UPS.Input.ConfigVoltage", 230);
        Set("UPS.Input.Voltage", 232);
        Set("UPS.Input.LowVoltageTransfer", 180);
        Set("UPS.Input.HighVoltageTransfer", 266);
        Set("UPS.Input.APCSensitivity", 1);
        Set("UPS.PowerConverter.PercentLoad", 12);
        Set("UPS.PowerConverter.ConfigActivePower", 405);

        var cap = new HidCapture
        {
            Source = "synthetic APC Back-UPS (HID Power Device spec)",
            VendorId = 0x051d, ProductId = 0x0002, Vendor = ApcVendor, Product = product,
            Objects = d.Fields.Count, Descriptor = descriptor,
        };
        cap.Strings[StrProduct] = product;
        cap.Strings[StrSerial] = ApcSerial;
        cap.Strings[StrVendor] = ApcVendor;
        cap.Strings[StrChemistry] = "PbAc";
        foreach (var f in d.Fields)
        {
            var rep = reports[(f.Type, f.ReportId)];
            cap.Items.Add(new HidCapture.Item((byte[])rep.Clone(), HidUsageTables.PathToString(f.Path), f.Type, f.ReportId,
                f.Offset, f.Size, f.ToPhysical(f.Extract(rep))));
        }
        return cap;

        static byte[] Init(byte[] r, byte id) { r[0] = id; return r; }
    }
}
