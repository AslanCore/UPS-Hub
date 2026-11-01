// HID report descriptor parser and value extraction.
// Ported from Network UPS Tools drivers/hidparser.c and drivers/libhid.c
// (GPL-2.0-or-later; see NOTICE.md). Behaviour, quirks included, follows NUT so the
// same devices decode to the same values.

namespace UpsHub.Core.Local.Hid;

public enum HidReportType : byte { Input = 0x80, Output = 0x90, Feature = 0xB0 }

/// <summary>One data field of a report (NUT's HIDData_t).</summary>
public sealed class HidField
{
    /// <summary>Usage path from the top collection down to the field, as (page &lt;&lt; 16 | usage) nodes.</summary>
    public required uint[] Path { get; init; }
    public required HidReportType Type { get; init; }
    public required byte ReportId { get; init; }
    /// <summary>Bit offset after the report ID byte.</summary>
    public required int Offset { get; init; }
    public required int Size { get; init; }
    public byte Attribute { get; init; }
    public long LogMin { get; set; }
    public long LogMax { get; set; }
    /// <summary>LogMax was mis-encoded (looked negative) and was re-read as unsigned.</summary>
    public bool AssumedLogMax { get; set; }
    public long PhyMin { get; init; }
    public long PhyMax { get; init; }
    public bool HavePhyMin { get; init; }
    public bool HavePhyMax { get; init; }
    public uint Unit { get; init; }
    public sbyte UnitExp { get; init; }
    /// <summary>Opaque handle for platform readers (e.g. the Windows HidP link collection).</summary>
    public object? Tag { get; init; }

    public uint Usage => Path.Length == 0 ? 0 : Path[^1];
    public bool IsConstant => (Attribute & 0x01) != 0;

    public override string ToString() =>
        $"{HidUsageTables.PathToString(Path)} {Type} id=0x{ReportId:x2} off={Offset} size={Size} log={LogMin}..{LogMax}";

    // ---- value extraction (NUT GetValue) -----------------------------------------------------

    private static int HiBit(ulong x)
    {
        int res = 0;
        while (x > 0xff) { x >>= 8; res += 8; }
        while (x != 0) { x >>= 1; res++; }
        return res;
    }

    /// <summary>Extracts the logical value from a full report buffer (byte 0 = report ID).</summary>
    public long Extract(ReadOnlySpan<byte> report)
    {
        long value = 0;
        int bit = Offset + 8;
        for (int w = 0; w < Size; w++, bit++)
        {
            int idx = bit >> 3;
            if (idx >= report.Length) break;
            if ((report[idx] & (1 << (bit & 7))) != 0) value += 1L << w;
        }
        return Normalize(value);
    }

    /// <summary>Masks, sign-extends and clamps a raw field value to LogMin..LogMax (NUT semantics).</summary>
    public long Normalize(long value)
    {
        ulong magMax = LogMax >= 0 ? (ulong)LogMax : (ulong)(-(LogMax + 1));
        ulong magMin = LogMin >= 0 ? (ulong)LogMin : (ulong)(-(LogMin + 1));
        int hb = HiBit(magMax > magMin ? magMax : magMin);
        ulong signbit = hb >= 63 ? 1UL << 63 : 1UL << hb;
        ulong mask = (signbit - 1) | (LogMin < 0 ? signbit : 0);
        value = (long)((ulong)value & mask);
        if (LogMin < 0 && ((ulong)value & signbit) != 0) value |= (long)~mask;
        if (value < LogMin) value = LogMin;
        else if (value > LogMax) value = LogMax;
        return value;
    }

    /// <summary>Writes a logical value into a report buffer (NUT SetValue). Used by the replay device.</summary>
    public void Write(Span<byte> report, long value)
    {
        int bit = Offset + 8;
        for (int w = 0; w < Size; w++, bit++)
        {
            int idx = bit >> 3;
            if (idx >= report.Length) break;
            byte m = (byte)(1 << (bit & 7));
            if ((value & (1L << w)) != 0) report[idx] |= m; else report[idx] &= (byte)~m;
        }
    }

    // ---- logical -> physical (NUT logical_to_physical + unit exponent) ------------------------

    private static readonly (uint Unit, int Expo)[] UnitExpo =
    {
        (0x00F0D121, 7), // volt
        (0x00100001, 0), // ampere
        (0x0000D121, 7), // VA / watt
        (0x00001001, 0), // second
        (0x00010001, 0), // kelvin
        (0x0000F001, 0), // hertz
        (0x00101001, 0), // ampere-second
    };

    public int EffectiveExponent
    {
        get
        {
            int e = UnitExp;
            foreach (var (u, x) in UnitExpo) if (u == Unit) { e -= x; break; }
            return e;
        }
    }

    public double ToPhysical(long logical)
    {
        double value = logical;
        bool scale = HavePhyMin && HavePhyMax && !(PhyMin == 0 && PhyMax == 0);
        if (scale && PhyMax > PhyMin && LogMax > LogMin)
        {
            double factor = (double)(PhyMax - PhyMin) / (LogMax - LogMin);
            value = (logical - LogMin) * factor + PhyMin;
        }
        int e = EffectiveExponent;
        return e == 0 ? value : value * Math.Pow(10, e);
    }
}

/// <summary>Parsed report descriptor (NUT's HIDDesc_t).</summary>
public sealed class HidReportDescriptor
{
    public List<HidField> Fields { get; } = new();
    /// <summary>Report payload length in bytes, by (type, id), excluding the ID byte.</summary>
    public Dictionary<(HidReportType, byte), int> ReportLengths { get; } = new();
    public List<string> Warnings { get; } = new();

    public int ReportLength(HidReportType type, byte id) => ReportLengths.GetValueOrDefault((type, id));

    public HidField? Find(string path, HidReportType type) =>
        Fields.FirstOrDefault(f => f.Type == type && HidUsageTables.PathToString(f.Path) == path);

    public HidField? FindByIdAndUsage(byte reportId, uint usage) =>
        Fields.FirstOrDefault(f => f.ReportId == reportId && f.Usage == usage);

    public static HidReportDescriptor Parse(ReadOnlySpan<byte> d)
    {
        var desc = new HidReportDescriptor();
        var usages = new List<uint>();
        var path = new List<uint>();
        var offsets = new Dictionary<(HidReportType, byte), int>();
        ushort upage = 0;
        byte reportId = 0;
        int reportSize = 0, reportCount = 0;
        long logMin = 0, logMax = 0, phyMin = 0, phyMax = 0;
        bool havePhyMin = false, havePhyMax = false, assumed = false;
        uint unit = 0;
        sbyte unitExp = 0;
        int pos = 0;

        static long Signed(uint v, int size) => size switch
        {
            1 => (sbyte)v,
            2 => (short)v,
            4 => (int)v,
            _ => v,
        };

        while (pos < d.Length)
        {
            byte item = d[pos++];
            if (item == 0xFE) // long item: skip
            {
                if (pos + 1 >= d.Length) break;
                int len = d[pos];
                pos += 2 + len;
                continue;
            }
            int size = (item & 3) == 3 ? 4 : item & 3;
            if (size > d.Length - pos) { desc.Warnings.Add("Truncated item at end of descriptor"); break; }
            uint value = 0;
            for (int i = 0; i < size; i++) value |= (uint)d[pos + i] << (8 * i);
            pos += size;

            switch (item & 0xFC)
            {
                case 0x04: upage = (ushort)value; break;                     // Usage Page
                case 0x08:                                                   // Usage
                    usages.Add(size > 2 ? value : ((uint)upage << 16) | (value & 0xFFFF));
                    break;
                case 0xA0:                                                   // Collection
                    path.Add(usages.Count > 0 ? usages[0] : 0);
                    if (value >= 0x80) path.Add(0x00ff0000 | (value & 0x7F));
                    usages.Clear();
                    break;
                case 0xC0:                                                   // End Collection
                    if (path.Count == 0) { desc.Warnings.Add("Unbalanced End Collection"); return desc; }
                    // NUT: drop the last node; if that was a collection index "[n]", drop its collection too.
                    var removed = path[^1];
                    path.RemoveAt(path.Count - 1);
                    if (path.Count > 0 && (removed & 0xffff0000) == 0x00ff0000) path.RemoveAt(path.Count - 1);
                    usages.Clear();
                    break;
                case 0x80: case 0x90: case 0xB0:                             // Input / Output / Feature
                {
                    var type = (HidReportType)(item & 0xFC);
                    int count = reportCount;
                    var key = (type, reportId);
                    for (int c = 0; c < count; c++)
                    {
                        uint usage = c < usages.Count ? usages[c] : 0;
                        int off = offsets.GetValueOrDefault(key);
                        if (usage != 0)
                        {
                            desc.Fields.Add(new HidField
                            {
                                Path = path.Append(usage).ToArray(),
                                Type = type, ReportId = reportId, Offset = off, Size = reportSize,
                                Attribute = (byte)value,
                                LogMin = logMin, LogMax = logMax, AssumedLogMax = assumed,
                                PhyMin = phyMin, PhyMax = phyMax, HavePhyMin = havePhyMin, HavePhyMax = havePhyMax,
                                Unit = unit, UnitExp = unitExp,
                            });
                        }
                        // NUT: a field without its own usage is padding / a continuation; it only advances the offset.
                        offsets[key] = off + reportSize;
                        int bytes = (off + reportSize + 7) >> 3;
                        if (bytes > desc.ReportLengths.GetValueOrDefault(key)) desc.ReportLengths[key] = bytes;
                    }
                    usages.Clear();
                    break;
                }
                case 0x84: reportId = (byte)value; break;
                case 0x74: reportSize = (int)value; break;
                case 0x94: reportCount = (int)value; break;
                case 0x14: logMin = Signed(value, size); break;
                case 0x24:
                    logMax = Signed(value, size);
                    assumed = false;
                    if (logMax < logMin)
                    {
                        // NUT hack: firmware encoded e.g. 0..65535 as 0..-1; read it as unsigned.
                        desc.Warnings.Add($"LogMax {logMax} < LogMin {logMin} in report 0x{reportId:x2}; read as {value}");
                        logMax = value;
                        assumed = true;
                    }
                    break;
                case 0x34: phyMin = Signed(value, size); havePhyMin = true; break;
                case 0x44: phyMax = Signed(value, size); havePhyMax = true; break;
                case 0x54: unitExp = (sbyte)(value > 7 ? (int)(value | 0xF0) - 256 : (int)value); break;
                case 0x64: unit = value; break;
            }
        }
        return desc;
    }

    public static byte[] ParseHex(string hex) =>
        hex.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();
}
