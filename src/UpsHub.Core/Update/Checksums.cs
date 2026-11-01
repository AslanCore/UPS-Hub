using System.Security.Cryptography;

namespace UpsHub.Core.Update;

/// <summary><c>sha256sum</c>-style checksum files: "&lt;64 hex&gt;  &lt;file name&gt;" (a '*' before the name marks binary mode).</summary>
public static class Checksums
{
    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66 || line.StartsWith('#')) continue;
            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var name = line[64..].TrimStart(' ', '\t');
            if (name.StartsWith('*')) name = name[1..];
            name = Path.GetFileName(name.Replace('\\', '/'));
            if (name.Length > 0) d[name] = hash.ToLowerInvariant();
        }
        return d;
    }

    public static string Format(IEnumerable<(string Name, string Sha256)> files) =>
        string.Concat(files.Select(f => $"{f.Sha256.ToLowerInvariant()}  {f.Name}\n"));

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }

    public static bool Matches(string expectedHex, string actualHex) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHex), Convert.FromHexString(actualHex));
}
