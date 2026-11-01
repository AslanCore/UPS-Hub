namespace UpsHub.Core.Model;

/// <summary>
/// Tokens of the NUT <c>ups.status</c> variable. Flags are powers of two (a plain enum broke
/// change detection in WinNUT, nutdotnet #71).
/// </summary>
[Flags]
public enum UpsStatus
{
    None = 0,
    OL = 1 << 0,       // on line (mains)
    OB = 1 << 1,       // on battery
    LB = 1 << 2,       // low battery
    HB = 1 << 3,       // high battery
    RB = 1 << 4,       // replace battery
    CHRG = 1 << 5,     // charging
    DISCHRG = 1 << 6,  // discharging
    BYPASS = 1 << 7,
    CAL = 1 << 8,      // calibrating
    OFF = 1 << 9,      // output off
    OVER = 1 << 10,    // overloaded
    TRIM = 1 << 11,
    BOOST = 1 << 12,
    FSD = 1 << 13,     // forced shutdown (set by upsmon primary)
    ALARM = 1 << 14,
    ECO = 1 << 15,
    WAIT = 1 << 16,    // driver still initialising
}

public static class UpsStatusParser
{
    public static UpsStatus Parse(string? raw)
    {
        var s = UpsStatus.None;
        if (string.IsNullOrWhiteSpace(raw)) return s;
        foreach (var token in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Enum.TryParse<UpsStatus>(token, ignoreCase: true, out var f) && f != UpsStatus.None)
                s |= f;
        // Unknown tokens are ignored on purpose.
        return s;
    }

    public static UpsHealth HealthOf(UpsStatus s) =>
        s.HasFlag(UpsStatus.FSD) ? UpsHealth.ForcedShutdown :
        s.HasFlag(UpsStatus.LB) ? UpsHealth.LowBattery :
        s.HasFlag(UpsStatus.OB) ? UpsHealth.OnBattery :
        s.HasFlag(UpsStatus.OVER) || s.HasFlag(UpsStatus.ALARM) || s.HasFlag(UpsStatus.RB) ? UpsHealth.Alarm :
        s.HasFlag(UpsStatus.OL) ? UpsHealth.Online : UpsHealth.Unknown;

    public static bool OnBattery(this UpsStatus s) => s.HasFlag(UpsStatus.OB);
    public static bool OnLine(this UpsStatus s) => s.HasFlag(UpsStatus.OL) && !s.HasFlag(UpsStatus.OB);
}

/// <summary>Coarse state used for colours, tray icon and the status pill.</summary>
public enum UpsHealth { Unknown, Online, OnBattery, LowBattery, ForcedShutdown, Alarm }
