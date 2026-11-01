using System.Globalization;

namespace UpsHub.Core.Update;

/// <summary>Semantic Versioning 2.0 (precedence rules of semver.org §11). Accepts a leading "v"; build metadata is ignored.</summary>
public sealed record SemVer(int Major, int Minor, int Patch, string[] Pre) : IComparable<SemVer>
{
    public static SemVer? TryParse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; if (pre.Length == 0) return null; }
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 3) return null;
        var n = new int[3];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        var preIds = pre?.Split('.') ?? Array.Empty<string>();
        if (preIds.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        return new SemVer(n[0], n[1], n[2], preIds);
    }

    public bool IsPrerelease => Pre.Length > 0;

    public int CompareTo(SemVer? o)
    {
        if (o is null) return 1;
        int c = Major.CompareTo(o.Major);
        if (c == 0) c = Minor.CompareTo(o.Minor);
        if (c == 0) c = Patch.CompareTo(o.Patch);
        if (c != 0) return c;
        // A pre-release sorts before its release: 1.0.0-rc.1 < 1.0.0.
        if (Pre.Length == 0 && o.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;
        if (o.Pre.Length == 0) return -1;
        for (int i = 0; i < Math.Min(Pre.Length, o.Pre.Length); i++)
        {
            bool an = Pre[i].All(char.IsAsciiDigit), bn = o.Pre[i].All(char.IsAsciiDigit);
            if (an && bn) c = BigCompare(Pre[i], o.Pre[i]);
            else if (an) c = -1;          // numeric identifiers sort before alphanumeric ones
            else if (bn) c = 1;
            else c = string.CompareOrdinal(Pre[i], o.Pre[i]);
            if (c != 0) return Math.Sign(c);
        }
        return Pre.Length.CompareTo(o.Pre.Length);
    }

    private static int BigCompare(string a, string b)
    {
        a = a.TrimStart('0'); b = b.TrimStart('0');
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    }

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre.Length > 0 ? "-" + string.Join('.', Pre) : "");
    public bool Equals(SemVer? o) => o is not null && CompareTo(o) == 0;
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Pre));
}
