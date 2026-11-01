using System.Security.Cryptography;
using System.Text;

namespace UpsHub.Core.Update;

/// <summary>
/// minisign-compatible keys and signatures (https://jedisct1.github.io/minisign/). Signatures made with
/// the release tool can be checked with <c>minisign -Vm SHA256SUMS.txt -P &lt;public key&gt;</c>, and
/// signatures made with the minisign CLI verify here. Both "ED" (BLAKE2b-prehashed, the default) and
/// legacy "Ed" signatures are accepted; the trusted comment is verified too.
/// </summary>
public static class Minisign
{
    public sealed record PublicKey(byte[] KeyId, byte[] Key)
    {
        public string KeyIdHex => Convert.ToHexString(KeyId.Reverse().ToArray());
        /// <summary>The one-line form used in minisign.pub and embedded in the app.</summary>
        public string Encoded => Convert.ToBase64String([(byte)'E', (byte)'d', .. KeyId, .. Key]);
    }

    public sealed record SecretKey(byte[] KeyId, byte[] Seed)
    {
        public PublicKey Public => new(KeyId, Ed25519.PublicKey(Seed));
        /// <summary>Base64 of key id + seed; what the UPSHUB_SIGNING_KEY secret holds.</summary>
        public string Encoded => Convert.ToBase64String([.. KeyId, .. Seed]);

        public static SecretKey Generate() => new(RandomNumberGenerator.GetBytes(8), RandomNumberGenerator.GetBytes(32));

        public static SecretKey Parse(string text)
        {
            var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("untrusted comment:", StringComparison.Ordinal))
                       ?? throw new FormatException("Empty signing key.");
            var b = Convert.FromBase64String(line);
            if (b.Length != 40) throw new FormatException("A UPS Hub signing key is 40 bytes (key id + Ed25519 seed).");
            return new SecretKey(b[..8], b[8..]);
        }

        public string ToFileText() =>
            "untrusted comment: UPS Hub release signing key (key id + Ed25519 seed). KEEP PRIVATE, never commit.\n" + Encoded + "\n";
    }

    public sealed record Signature(string Algorithm, byte[] KeyId, byte[] Sig, string TrustedComment, byte[] GlobalSig);

    public static PublicKey ParsePublicKey(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("untrusted comment:", StringComparison.Ordinal))
                   ?? throw new FormatException("Empty public key.");
        var b = Convert.FromBase64String(line);
        if (b.Length != 42 || b[0] != 'E' || b[1] != 'd') throw new FormatException("Not a minisign Ed25519 public key.");
        return new PublicKey(b[2..10], b[10..]);
    }

    public static string PublicKeyFileText(PublicKey k) => $"untrusted comment: minisign public key {k.KeyIdHex}\n{k.Encoded}\n";

    public static Signature ParseSignature(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length < 4 || !lines[0].StartsWith("untrusted comment:", StringComparison.Ordinal))
            throw new FormatException("Not a minisign signature (no untrusted comment).");
        var sig = Convert.FromBase64String(lines[1].Trim());
        if (sig.Length != 74) throw new FormatException("Bad minisign signature length.");
        const string tc = "trusted comment: ";
        if (!lines[2].StartsWith(tc, StringComparison.Ordinal)) throw new FormatException("Missing trusted comment.");
        var global = Convert.FromBase64String(lines[3].Trim());
        if (global.Length != 64) throw new FormatException("Bad minisign global signature length.");
        var alg = Encoding.ASCII.GetString(sig, 0, 2);
        if (alg is not ("Ed" or "ED")) throw new FormatException($"Unsupported minisign algorithm '{alg}'.");
        return new Signature(alg, sig[2..10], sig[10..], lines[2][tc.Length..], global);
    }

    /// <summary>True if <paramref name="signatureText"/> is a valid signature of <paramref name="data"/> by <paramref name="key"/>.</summary>
    public static bool Verify(PublicKey key, ReadOnlySpan<byte> data, string signatureText, out string? error)
    {
        Signature s;
        try { s = ParseSignature(signatureText); }
        catch (FormatException ex) { error = ex.Message; return false; }
        if (!s.KeyId.AsSpan().SequenceEqual(key.KeyId))
        {
            error = $"Signed with a different key ({Convert.ToHexString(s.KeyId.Reverse().ToArray())}, expected {key.KeyIdHex}).";
            return false;
        }
        var msg = s.Algorithm == "ED" ? Blake2b.Hash(data) : data.ToArray();
        if (!Ed25519.Verify(key.Key, msg, s.Sig)) { error = "The signature does not match the file."; return false; }
        if (!Ed25519.Verify(key.Key, [.. s.Sig, .. Encoding.UTF8.GetBytes(s.TrustedComment)], s.GlobalSig))
        { error = "The signature's trusted comment was altered."; return false; }
        error = null;
        return true;
    }

    /// <summary>Signs like <c>minisign -S</c> (prehashed "ED").</summary>
    public static string Sign(SecretKey key, ReadOnlySpan<byte> data, string trustedComment, string untrustedComment = "signature from UPS Hub release key")
    {
        if (trustedComment.Contains('\n')) throw new ArgumentException("Trusted comment must be one line.");
        var sig = Ed25519.Sign(key.Seed, Blake2b.Hash(data));
        var global = Ed25519.Sign(key.Seed, [.. sig, .. Encoding.UTF8.GetBytes(trustedComment)]);
        return $"untrusted comment: {untrustedComment}\n{Convert.ToBase64String([(byte)'E', (byte)'D', .. key.KeyId, .. sig])}\n" +
               $"trusted comment: {trustedComment}\n{Convert.ToBase64String(global)}\n";
    }
}
