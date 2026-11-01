// Megatec / Q1 serial protocol (the "blazer" family). Status bits and battery-charge estimate
// follow Network UPS Tools drivers/nutdrv_qx_megatec.c, nutdrv_qx_blazer-common.c, nutdrv_qx.c
// (GPL-2.0-or-later).

using System.Globalization;

namespace UpsHub.Core.Local;

/// <summary>A line-oriented serial link: send a command, get one CR-terminated reply.</summary>
public interface ISerialLine : IDisposable
{
    /// <summary>Returns the reply without the trailing CR, or null on timeout.</summary>
    string? Query(string command, TimeSpan timeout);
}

public static class MegatecProtocol
{
    public sealed record Q1(double InputV, double FaultV, double OutputV, double LoadPct, double Freq,
        double BattV, double TempC, string Bits);

    public sealed record Rating(double Voltage, double Current, double BattV, double Freq);

    private static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>"(MMM.M NNN.N PPP.P QQQ RR.R S.SS TT.T b7b6b5b4b3b2b1b0"</summary>
    public static Q1? ParseQ1(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply[0] != '(') return null;
        var p = reply[1..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 8 || p[7].Length != 8 || p[7].Any(c => c is not ('0' or '1'))) return null;
        try { return new Q1(D(p[0]), D(p[1]), D(p[2]), D(p[3]), D(p[4]), D(p[5]), D(p[6]), p[7]); }
        catch (FormatException) { return null; }
    }

    /// <summary>"#MMM.M QQQ SS.SS RR.R" (rated voltage, current, battery voltage, frequency).</summary>
    public static Rating? ParseF(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply[0] != '#') return null;
        var p = reply[1..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 4) return null;
        try { return new Rating(D(p[0]), D(p[1]), D(p[2]), D(p[3])); }
        catch (FormatException) { return null; }
    }

    /// <summary>"#Company_Name UPS_Model Version" in fixed 15/10/10 columns.</summary>
    public static (string Mfr, string Model, string Version)? ParseI(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply[0] != '#' || reply.Length < 2) return null;
        var s = reply[1..].PadRight(37);
        return (s[..15].Trim(), s.Substring(16, 10).Trim(), s.Substring(27, 10).Trim());
    }

    public static Dictionary<string, string> ToVariables(Q1 q, Rating? rating, (string Mfr, string Model, string Version)? info)
    {
        static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        var v = new Dictionary<string, string>
        {
            ["device.type"] = "ups",
            ["driver.name"] = "megatec",
            ["input.voltage"] = F1(q.InputV),
            ["input.voltage.fault"] = F1(q.FaultV),
            ["output.voltage"] = F1(q.OutputV),
            ["ups.load"] = q.LoadPct.ToString("0", CultureInfo.InvariantCulture),
            ["input.frequency"] = F1(q.Freq),
        };
        if (q.TempC > 0) v["ups.temperature"] = F1(q.TempC);
        if (info is { } i)
        {
            if (i.Mfr.Length > 0) v["device.mfr"] = v["ups.mfr"] = i.Mfr;
            if (i.Model.Length > 0) v["device.model"] = v["ups.model"] = i.Model;
            if (i.Version.Length > 0) v["ups.firmware"] = i.Version;
        }

        // Battery: some units report volts per cell (2.05), others the whole string (27.0).
        double battV = q.BattV;
        if (rating is { } r)
        {
            v["battery.voltage.nominal"] = F1(r.BattV);
            v["input.voltage.nominal"] = r.Voltage.ToString("0", CultureInfo.InvariantCulture);
            v["input.frequency.nominal"] = F1(r.Freq);
            if (battV < 3 && r.BattV > 3) battV *= Math.Round(r.BattV / 2);   // cells of 2 V
            double low = 104 * r.BattV / 120, high = 130 * r.BattV / 120;
            var charge = Math.Clamp(100 * (battV - low) / (high - low), 0, 100);
            v["battery.charge"] = charge.ToString("0", CultureInfo.InvariantCulture);
            v["battery.charge.estimated"] = "1";
        }
        v["battery.voltage"] = battV.ToString("0.00", CultureInfo.InvariantCulture);

        var b = q.Bits;
        var st = new List<string> { b[0] == '1' ? "OB" : "OL" };
        if (b[1] == '1') st.Add("LB");
        if (b[2] == '1')
        {
            if (q.OutputV < 0.95 * q.InputV) st.Add("TRIM");
            else if (q.OutputV < 1.05 * q.InputV) st.Add("BYPASS");
            else if (q.OutputV < 1.5 * q.InputV) st.Add("BOOST");
        }
        if (b[5] == '1') st.Add("CAL");
        if (b[6] == '1') st.Add("FSD");
        if (b[3] == '1') { v["ups.alarm"] = "UPS selftest failed!"; st.Add("ALARM"); }
        v["ups.type"] = b[4] == '1' ? "offline / line interactive" : "online";
        v["ups.beeper.status"] = b[7] == '1' ? "enabled" : "disabled";
        v["ups.status"] = string.Join(" ", st);
        return v;
    }
}

/// <summary>Tier 2: Megatec/Q1 over a serial or USB-serial port.</summary>
public sealed class MegatecSource : ILocalUpsSource
{
    private readonly Func<ISerialLine> _open;
    private ISerialLine? _line;
    private MegatecProtocol.Rating? _rating;
    private (string, string, string)? _info;
    private int _misses;

    public MegatecSource(Func<ISerialLine> open, string port) { _open = open; Kind = "Serial " + port; }
    public string Kind { get; }
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1.5);

    public void Open()
    {
        _line = _open();
        var q = MegatecProtocol.ParseQ1(_line.Query("Q1", Timeout))
                ?? throw new IOException("No Megatec/Q1 reply on this port.");
        _rating = MegatecProtocol.ParseF(_line.Query("F", Timeout));
        _info = MegatecProtocol.ParseI(_line.Query("I", Timeout));
        _ = q;
    }

    public IReadOnlyDictionary<string, string> Read()
    {
        var q = MegatecProtocol.ParseQ1((_line ?? throw new InvalidOperationException("Not open")).Query("Q1", Timeout));
        if (q is null)
        {
            if (++_misses >= 3) throw new IOException("The UPS stopped answering on the serial port.");
            throw new IOException("Garbled reply from the UPS.");
        }
        _misses = 0;
        return MegatecProtocol.ToVariables(q, _rating, _info);
    }

    public void Dispose() { _line?.Dispose(); _line = null; }
}

/// <summary>The synthetic APC Back-UPS fixture, animated: the demo and Linux preview source.</summary>
public sealed class ReplayUpsSource : ILocalUpsSource
{
    private readonly Func<Hid.HidCapture> _fixture;
    private readonly Action<Hid.ReplayHidDevice, int>? _script;
    private Hid.HidUpsDriver? _driver;
    private int _tick;

    /// <param name="script">Called before each read with the tick number; default keeps the UPS on line with a little jitter.</param>
    public ReplayUpsSource(Func<Hid.HidCapture>? fixture = null, Action<Hid.ReplayHidDevice, int>? script = null)
    {
        _fixture = fixture ?? (() => Hid.SyntheticHid.ApcBackUps());
        _script = script;
    }

    public string Kind => "USB HID (simulated)";
    public Hid.ReplayHidDevice? Device { get; private set; }

    public void Open()
    {
        Device = Hid.ReplayHidDevice.Create(_fixture());
        Device.Apply(acPresent: true, charging: false, charge: 100, runtime: 2580, load: 21, inputVoltage: 231);
        _driver = new Hid.HidUpsDriver(Device);
    }

    public IReadOnlyDictionary<string, string> Read()
    {
        var d = Device ?? throw new InvalidOperationException("Not open");
        var t = _tick++;
        if (_script is not null) _script(d, t);
        else
        {
            double load = 21 + 3 * Math.Sin(t / 4.0) + (t % 7 == 0 ? 2 : 0);
            d.Apply(load: Math.Round(load), inputVoltage: 231 + (t % 5 == 0 ? 1 : 0), runtime: Math.Round(2580 * 21 / load));
        }
        return _driver!.Poll();
    }

    public void Dispose() { Device?.Dispose(); Device = null; _driver = null; }
}
